namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Curator.Catalog;
using Curator.Library;
using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class TrophyMatchServiceTests
{
    private const int AccessTokenLifetimeSeconds = 3600;

    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly string MatchedNpCommunicationId = TestValues.NewNpCommunicationId();

    private static readonly string ExactMatchTitleId = TestValues.NewTitleId();

    private static readonly string ExactMatchTitleName = TestValues.NewLongTitle();

    private static readonly string ExactMatchBody = TitlesBody(
        new PsnTitleTrophyTitles
        {
            NpTitleId = ExactMatchTitleId,
            TrophyTitles = [Trophy(ExactMatchTitleName, NewProgress())],
        });

    private static readonly string IdentitySub = TestValues.NewIdentitySub();

    [Fact]
    public async Task MatchTrophiesAsync_SkipsTheWholeStage_WhenTheUserHasNotOptedIntoTrophyHarvesting()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            repository,
            new PsnTrophyClient(),
            null,
            IdentitySub,
            [Game(TestValues.NewLongTitle())],
            [NewGameId()],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.AttemptedCount);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task MatchTrophiesAsync_RejectsMismatchedGameAndIdLists()
    {
        // Arrange
        var repository = new LibraryRepository(new FakeDbDataSource());

        // Act
        var exception = await Record.ExceptionAsync(() => TrophyMatchService.MatchTrophiesAsync(
            repository,
            new PsnTrophyClient(),
            null,
            IdentitySub,
            [Game(TestValues.NewLongTitle())],
            [NewGameId(), NewGameId()],
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task MatchTrophiesAsync_AttemptsNothing_WhenEveryGameAlreadyCarriesAPersistedMatch()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable()));
        var handler = StubHttpMessageHandler.Always(() => Json(NoTrophyTitlesBody()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game(TestValues.NewLongTitle())],
            [NewGameId()],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.AttemptedCount);
    }

    [Fact]
    public async Task MatchTrophiesAsync_ResolvesAPs4TitleThroughTheExactLookup()
    {
        // Arrange
        var gameId = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(ExactMatchBody));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game(ExactMatchTitleName, ExactMatchTitleId)],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.ExactMatchedCount);
        Assert.Equal(0, result.FuzzyMatchedCount);
    }

    [Fact]
    public async Task MatchTrophiesAsync_RecordsTheExactMatchAsSuch()
    {
        // Arrange
        var gameId = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(ExactMatchBody));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game(ExactMatchTitleName, ExactMatchTitleId)],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        var update = dataSource.ExecutedCommands[1];
        Assert.Equal(TrophyMatchService.ExactMatchMethod, update.Parameters["@method"].Value);
        Assert.Equal(MatchedNpCommunicationId, update.Parameters["@np_communication_id"].Value);
    }

    [Fact]
    public async Task MatchTrophiesAsync_AsksPsnForTitlesInBatches_RatherThanOneCallPerPs4Game()
    {
        // Arrange
        var oneMoreGameThanFitsInASingleBatch = PsnTrophyClient.TitleBatchSize + 1;
        var gameIds = Enumerable
            .Range(0, oneMoreGameThanFitsInASingleBatch)
            .Select(_ => NewGameId())
            .ToArray();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameIds)));
        var handler = StubHttpMessageHandler.Always(() => Json(TitlesBody()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();
        var games = TestValues
            .NewDistinctTitleIds(oneMoreGameThanFitsInASingleBatch)
            .Select(titleId => Game(TestValues.NewLongTitle(), titleId))
            .ToArray();

        // Act
        await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            games,
            gameIds,
            TestContext.Current.CancellationToken);

        // Assert
        var batchRequests = handler.Requests
            .Select(request => request.RequestUri)
            .OfType<Uri>()
            .Where(requestedUri => requestedUri.AbsolutePath.EndsWith("titles/trophyTitles", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, batchRequests.Count);
        Assert.Equal(
            PsnTrophyClient.TitleBatchSize,
            Uri.UnescapeDataString(batchRequests[0].Query).Split(',').Length);
    }

    [Fact]
    public async Task MatchTrophiesAsync_SkipsTheExactLookupForAPs5Title()
    {
        // Arrange
        var gameId = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(NoTrophyTitlesBody()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game(TestValues.NewLongTitle(), TestValues.NewPs5TitleId())],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            handler.Requests,
            request => request.RequestUri is { } requestedUri
                && requestedUri.AbsolutePath.Contains("titles/trophyTitles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchTrophiesAsync_FallsBackToFuzzyMatching_WhenTheExactLookupResolvesNothing()
    {
        // Arrange
        var gameId = Guid.NewGuid().ToString();
        var sharedTitle = TestValues.NewLongTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(TrophyTitlesBody(sharedTitle, NewProgress())));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game(sharedTitle, TestValues.NewPs5TitleId())],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.FuzzyMatchedCount);
        Assert.Equal(0, result.ExactMatchedCount);
    }

    [Fact]
    public async Task MatchTrophiesAsync_StampsAnAttemptEvenWhenNothingMatched_SoItIsNotRetriedForever()
    {
        // Arrange
        var gameId = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var gameTitle = TestValues.NewTokenFromFirstHalfOfAlphabet(24);
        var trophyTitleSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(24);
        var handler = StubHttpMessageHandler.Always(
            () => Json(TrophyTitlesBody(trophyTitleSharingNoCharactersWithIt, NewProgress())));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game(gameTitle, TestValues.NewPs5TitleId())],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.FuzzyMatchedCount);
        Assert.Equal(DBNull.Value, dataSource.ExecutedCommands[1].Parameters["@np_communication_id"].Value);
    }

    [Fact]
    public async Task MatchTrophiesAsync_CountsEveryGameItAttempted_MatchedOrNot()
    {
        // Arrange
        var first = NewGameId();
        var second = NewGameId();
        var games = new[]
        {
            Game(TestValues.NewLongTitle(), TestValues.NewPs5TitleId()),
            Game(TestValues.NewLongTitle(), TestValues.NewPs5TitleId()),
        };
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(first, second)));
        var handler = StubHttpMessageHandler.Always(() => Json(NoTrophyTitlesBody()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            games,
            [first, second],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.AttemptedCount);
    }

    [Fact]
    public async Task MatchTrophiesAsync_RefreshesStoredProgressForTheWholeMatchedLibrary()
    {
        // Arrange
        var rowsTheRefreshUpdates = Random.Shared.Next(1, 100);
        var sharedTitle = TestValues.NewLongTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable()));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(rowsTheRefreshUpdates));
        var handler = StubHttpMessageHandler.Always(() => Json(TrophyTitlesBody(sharedTitle, NewProgress())));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game(sharedTitle)],
            [NewGameId()],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rowsTheRefreshUpdates, result.ProgressUpdatedCount);
    }

    private static string NewGameId() => Guid.NewGuid().ToString();

    private static int NewProgress() => Random.Shared.Next(1, 100);

    private static PsnTrophyTitle Trophy(string name, int progress) =>
        new() { NpCommunicationId = MatchedNpCommunicationId, TrophyTitleName = name, Progress = progress };

    private static string TitlesBody(params PsnTitleTrophyTitles[] titles) =>
        JsonSerializer.Serialize(new PsnTitleTrophyTitlesResponse { Titles = titles }, PsnWireFormat);

    private static string TrophyTitlesBody(string name, int progress) =>
        JsonSerializer.Serialize(
            new PsnTrophyTitlesResponse { TrophyTitles = [Trophy(name, progress)], NextOffset = 0 },
            PsnWireFormat);

    private static string NoTrophyTitlesBody() =>
        JsonSerializer.Serialize(new PsnTrophyTitlesResponse { NextOffset = 0 }, PsnWireFormat);

    private static CanonicalGame Game(string title, string? winningTitleId = null) =>
        new(
            title,
            NativePs5: true,
            Ps4Eligible: false,
            TestValues.NewFranchiseName(),
            ProductId: null,
            ConceptIds: [],
            WinningEntitlementId: TestValues.NewEntitlementId())
        {
            WinningTitleId = winningTitleId,
        };

    private static DataTable UnmatchedTable(params string[] gameIds)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        foreach (var gameId in gameIds)
        {
            table.Rows.Add(Guid.Parse(gameId));
        }

        return table;
    }

    private static async Task<PsnSession> ReadySessionAsync(StubHttpMessageHandler handler) =>
        await PsnSession.RestoreAsync(
            null,
            SeededStore(),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

    private static InMemoryPsnTokenStore SeededStore()
    {
        var store = new InMemoryPsnTokenStore();
        store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = TestValues.NewAccessToken(),
                ExpiresIn = AccessTokenLifetimeSeconds,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
