namespace Functions.Tests.Unit;

using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Functions.Curator;
using Functions.Curator.Catalog;
using Functions.Curator.Library;
using Functions.Curator.Psn;
using Functions.Tests.Unit.TestSupport;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class TrophyMatchServiceTests
{
    private static readonly int AccessTokenLifetimeSeconds = Generated.NewExpiresInSeconds();

    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly string MatchedNpCommunicationId = Generated.NewNpCommunicationId();

    private static readonly string ExactMatchTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);

    private static readonly string ExactMatchTitleName = Generated.NewLongTitle();

    private static readonly string ExactMatchBody = TitlesBody(
        new PsnTitleTrophyTitles
        {
            NpTitleId = ExactMatchTitleId,
            TrophyTitles = [Trophy(ExactMatchTitleName, NewTrophyProgress())],
        });

    private static readonly Guid IdentitySub = Generated.NewIdentitySub();

    [Fact]
    public async Task MatchTrophiesAsync_SkipsTheWholeStage_WhenTheUserHasNotOptedIntoTrophyHarvesting()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        var result = await new TrophyMatchService(repository, new PsnTrophyClient()).MatchTrophiesAsync(
            null,
            IdentitySub,
            [Game(Generated.NewLongTitle())],
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
        var exception = await Record.ExceptionAsync(() => new TrophyMatchService(repository, new PsnTrophyClient()).MatchTrophiesAsync(
            null,
            IdentitySub,
            [Game(Generated.NewLongTitle())],
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
        var result = await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            [Game(Generated.NewLongTitle())],
            [NewGameId()],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.AttemptedCount);
    }

    [Fact]
    public async Task MatchTrophiesAsync_ResolvesAPs4TitleThroughTheExactLookup()
    {
        // Arrange
        var gameId = NewGameId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(ExactMatchBody));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
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
    public async Task MatchTrophiesAsync_ResolvesAPs4TitleThroughTheExactLookup_WhateverTheCaseOfItsPrefix()
    {
        // Arrange
        var gameId = NewGameId();
        var lowercasePs4TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix).ToLowerInvariant();
        var lowercaseExactMatchBody = TitlesBody(
            new PsnTitleTrophyTitles
            {
                NpTitleId = lowercasePs4TitleId,
                TrophyTitles = [Trophy(ExactMatchTitleName, NewTrophyProgress())],
            });
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(lowercaseExactMatchBody));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            [Game(ExactMatchTitleName, lowercasePs4TitleId)],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.ExactMatchedCount);
    }

    [Fact]
    public async Task MatchTrophiesAsync_RecordsTheExactMatchAsSuch()
    {
        // Arrange
        var gameId = NewGameId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(ExactMatchBody));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            [Game(ExactMatchTitleName, ExactMatchTitleId)],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        var update = dataSource.ExecutedCommands[1];
        Assert.Equal(TrophyMatchService.ExactMatchMethod, update.Parameters[CuratorSqlParameters.Method].Value);
        Assert.Equal(MatchedNpCommunicationId, update.Parameters[CuratorSqlParameters.NpCommunicationId].Value);
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
        var games = Generated.NewDistinctTitleIds(TitlePlatform.Ps4TitleIdPrefix, oneMoreGameThanFitsInASingleBatch)
            .Select(titleId => Game(Generated.NewLongTitle(), titleId))
            .ToArray();

        // Act
        await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            games,
            gameIds,
            TestContext.Current.CancellationToken);

        // Assert
        var batchRequests = handler.Requests
            .Select(request => request.RequestUri)
            .OfType<Uri>()
            .Where(requestedUri => requestedUri.AbsolutePath.EndsWith(PsnTrophyClient.TitleTrophyTitlesRoute, StringComparison.Ordinal))
            .ToList();
        Assert.Collection(
            batchRequests,
            fullBatch => Assert.Equal(PsnTrophyClient.TitleBatchSize, Uri.UnescapeDataString(fullBatch.Query).Split(',').Length),
            overflowBatch => Assert.Single(Uri.UnescapeDataString(overflowBatch.Query).Split(',')));
    }

    [Fact]
    public async Task MatchTrophiesAsync_SkipsTheExactLookupForAPs5Title()
    {
        // Arrange
        var gameId = NewGameId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(NoTrophyTitlesBody()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            [Game(Generated.NewLongTitle(), Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix))],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            handler.Requests,
            request => request.RequestUri is { } requestedUri
                && requestedUri.AbsolutePath.Contains(PsnTrophyClient.TitleTrophyTitlesRoute, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchTrophiesAsync_FallsBackToFuzzyMatching_WhenTheExactLookupResolvesNothing()
    {
        // Arrange
        var gameId = NewGameId();
        var sharedTitle = Generated.NewLongTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var handler = StubHttpMessageHandler.Always(() => Json(TrophyTitlesBody(sharedTitle, NewTrophyProgress())));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            [Game(sharedTitle, Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix))],
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
        var gameId = NewGameId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(gameId)));
        var gameTitle = Generated.NewTokenFromFirstHalfOfAlphabet(24);
        var trophyTitleSharingNoCharactersWithIt = Generated.NewTokenFromSecondHalfOfAlphabet(24);
        var handler = StubHttpMessageHandler.Always(
            () => Json(TrophyTitlesBody(trophyTitleSharingNoCharactersWithIt, NewTrophyProgress())));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            [Game(gameTitle, Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix))],
            [gameId],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.FuzzyMatchedCount);
        Assert.Equal(DBNull.Value, dataSource.ExecutedCommands[1].Parameters[CuratorSqlParameters.NpCommunicationId].Value);
    }

    [Fact]
    public async Task MatchTrophiesAsync_CountsEveryGameItAttempted_MatchedOrNot()
    {
        // Arrange
        var first = NewGameId();
        var second = NewGameId();
        var games = new[]
        {
            Game(Generated.NewLongTitle(), Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix)),
            Game(Generated.NewLongTitle(), Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix)),
        };
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable(first, second)));
        var handler = StubHttpMessageHandler.Always(() => Json(NoTrophyTitlesBody()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            games,
            [first, second],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(games.Length, result.AttemptedCount);
    }

    [Fact]
    public async Task MatchTrophiesAsync_RefreshesStoredProgressForTheWholeMatchedLibrary()
    {
        // Arrange
        var rowsTheRefreshUpdates = Random.Shared.Next(1, 100);
        var sharedTitle = Generated.NewLongTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(UnmatchedTable()));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(rowsTheRefreshUpdates));
        var handler = StubHttpMessageHandler.Always(() => Json(TrophyTitlesBody(sharedTitle, NewTrophyProgress())));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var result = await new TrophyMatchService(new LibraryRepository(dataSource), client).MatchTrophiesAsync(
            session,
            IdentitySub,
            [Game(sharedTitle)],
            [NewGameId()],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rowsTheRefreshUpdates, result.ProgressUpdatedCount);
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
            Generated.NewFranchiseName(),
            ProductId: null,
            ConceptIds: [],
            WinningEntitlementId: Generated.NewEntitlementId())
        {
            WinningTitleId = winningTitleId,
        };

    private static DataTable UnmatchedTable(params Guid[] gameIds)
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid));
        foreach (var gameId in gameIds)
        {
            table.Rows.Add(gameId);
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
                AccessToken = Generated.NewAccessToken(),
                ExpiresIn = AccessTokenLifetimeSeconds,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage Json(string body) => JsonResponse.Ok(body);
}
