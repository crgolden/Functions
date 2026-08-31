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
    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly string MatchedNpCommunicationId = $"NPWR{Random.Shared.Next(1000, 9999)}";

    private static readonly string ExactMatchBody = TitlesBody(
        new PsnTitleTrophyTitles
        {
            NpTitleId = "CUSA00900_00",
            TrophyTitles = [Trophy("Bloodborne", 73)],
        });

    private static readonly string IdentitySub = Guid.NewGuid().ToString();

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
            [Game("Bloodborne")],
            [Guid.NewGuid().ToString()],
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
            [Game("Bloodborne")],
            ["game-1", "game-2"],
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
            [Game("Bloodborne")],
            [Guid.NewGuid().ToString()],
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
            [Game("Bloodborne", "CUSA00900_00")],
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
            [Game("Bloodborne", "CUSA00900_00")],
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
        var gameIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameIds)));
        var handler = StubHttpMessageHandler.Always(() => Json(TitlesBody()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();
        var games = gameIds
            .Select((_, index) => Game($"Game {index}", $"CUSA0000{index}_00"))
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
        Assert.Equal(5, Uri.UnescapeDataString(batchRequests[0].Query).Split(',').Length);
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
            [Game("Astro Bot", "PPSA01234_00")],
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
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(TrophyTitlesBody("Astro Bot", 40)));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game("Astro Bot", "PPSA01234_00")],
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
        var handler = StubHttpMessageHandler.Always(() => Json(TrophyTitlesBody("Something Else Entirely", 40)));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game("Astro Bot", "PPSA01234_00")],
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
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
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
            [Game("Astro Bot", "PPSA01234_00"), Game("Returnal", "PPSA02222_00")],
            [first, second],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.AttemptedCount);
    }

    [Fact]
    public async Task MatchTrophiesAsync_RefreshesStoredProgressForTheWholeMatchedLibrary()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable()));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(3));
        var handler = StubHttpMessageHandler.Always(() => Json(TrophyTitlesBody("Bloodborne", 73)));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await TrophyMatchService.MatchTrophiesAsync(
            new LibraryRepository(dataSource),
            client,
            session,
            IdentitySub,
            [Game("Bloodborne")],
            [Guid.NewGuid().ToString()],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, result.ProgressUpdatedCount);
    }

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
            "None",
            ProductId: null,
            ConceptIds: [],
            WinningEntitlementId: "e1")
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
                AccessToken = "cached-access",
                ExpiresIn = 3600,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
