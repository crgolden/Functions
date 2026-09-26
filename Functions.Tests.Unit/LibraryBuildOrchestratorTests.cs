namespace Functions.Tests.Unit;

using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Functions.Curator;
using Functions.Curator.Catalog;
using Functions.Curator.Enrichment;
using Functions.Curator.Library;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;
using Functions.Tests.Unit.TestSupport;
using static Functions.Tests.Unit.LibraryBuildOrchestratorFixtureConstants;

[Trait("Category", "Unit")]
public sealed class LibraryBuildOrchestratorTests
{
    private static readonly int AccessTokenLifetimeSeconds = Generated.NewExpiresInSeconds();

    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task CanonicalizeAsync_IngestsThenAppliesCatalogRulesToProduceCanonicalGames()
    {
        // Arrange
        var ownedTitle = Generated.NewGameTitle();
        var ownedTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var harness = await HarnessAsync(Entitlements(OwnedGame(ownedTitle, ownedTitleId)));
        SeedIngestion(harness.IngestionDb, snapshotCount: 1);
        SeedEmptyCatalogRules(harness.CatalogDb);

        // Act
        var games = await harness.Orchestrator.CanonicalizeAsync(
            Generated.NewIdentitySub(),
            harness.Session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(games);
        Assert.Equal(ownedTitle, game.CanonicalTitle);
        Assert.True(game.NativePs5);
        Assert.Equal(ownedTitleId, game.WinningTitleId);
    }

    [Fact]
    public async Task RecordDownloadSizesAsync_WritesTheWebStoresPackageSizesThroughTheLibraryRepository()
    {
        // Arrange
        var entitlementId = Generated.NewPs3EntitlementId();
        var bytes = Generated.NewDownloadSizeBytes();
        var harness = await HarnessAsync(DownloadSizes(entitlementId, bytes));
        harness.LibraryDb.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var written = await harness.Orchestrator.RecordDownloadSizesAsync(
            Generated.NewIdentitySub(), harness.Session, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, written);
        var command = Assert.Single(harness.LibraryDb.ExecutedCommands);
        Assert.Contains("INSERT INTO game_download_sizes", command.ExecutedSql, StringComparison.Ordinal);
        var batch = Assert.IsType<string>(command.Parameters[CuratorSqlParameters.Batch].Value);
        var row = Assert.Single(Assert.IsType<List<EntitlementDownloadSize>>(
            JsonSerializer.Deserialize<List<EntitlementDownloadSize>>(batch, LibraryRepository.BatchFormat)));
        Assert.Equal(bytes, row.Bytes);
    }

    [Fact]
    public async Task RecordDownloadSizesAsync_RecordsTheFailureAndWritesNothing_WhenTheWebStoreCannotBeReached()
    {
        // Arrange
        var harness = await HarnessAsync();
        var unreachableSession = await ReadySessionAsync(
            StubHttpMessageHandler.Throws(new HttpRequestException(Generated.NewErrorMessage())));

        // Act
        var written = await harness.Orchestrator.RecordDownloadSizesAsync(
            Generated.NewIdentitySub(), unreachableSession, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, written);
        Assert.Empty(harness.LibraryDb.ExecutedCommands);
    }

    [Fact]
    public async Task PersistAndLinkAsync_UpsertsEachGameAndLinksItToTheIdentitysLibrary()
    {
        // Arrange
        var identitySub = Guid.NewGuid();
        var existingGameId = Guid.NewGuid();
        var harness = await HarnessAsync();
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(existingGameId));
        var game = Game(Generated.NewGameTitle(), [Generated.NewConceptId()]);

        // Act
        var gameIds = await harness.Orchestrator.PersistAndLinkAsync(
            identitySub, [game], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([existingGameId], gameIds);
        var insert = harness.LibraryDb.ExecutedCommands[0];
        Assert.Equal(identitySub, insert.Parameters[CuratorSqlParameters.IdentitySub].Value);
        var batch = Assert.IsType<string>(insert.Parameters[CuratorSqlParameters.Batch].Value);
        var row = Assert.Single(Assert.IsType<List<LibraryEntryRow>>(
            JsonSerializer.Deserialize<List<LibraryEntryRow>>(batch, LibraryRepository.BatchFormat)));
        Assert.Equal(existingGameId, row.GameId);
    }

    [Fact]
    public async Task EnrichDeltaAsync_RejectsMismatchedGamesAndIds()
    {
        // Arrange
        var harness = await HarnessAsync();
        var game = Game(Generated.NewGameTitle(), []);

        // Act
        var exception = await Record.ExceptionAsync(() => harness.Orchestrator.EnrichDeltaAsync(
            [game],
            [Generated.NewGameId(), Generated.NewGameId()],
            [],
            new EnrichmentCredentials(),
            cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public async Task EnrichDeltaAsync_EnrichesOnlyTheGamesTheRepositoryReportsAsUnenriched()
    {
        // Arrange
        var enrichedGameId = Guid.NewGuid();
        var unenrichedGameId = Guid.NewGuid();
        var harness = await HarnessAsync();
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable(unenrichedGameId)));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var candidates = new[]
        {
            Game(Generated.NewGameTitle(), []),
            Game(Generated.NewGameTitle(), []),
        };
        var gameIds = new[] { enrichedGameId, unenrichedGameId };

        // Act
        var result = await harness.Orchestrator.EnrichDeltaAsync(
            candidates, gameIds, [], new EnrichmentCredentials(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.EnrichedCount);
        var save = Assert.Single(harness.EnrichmentDb.ExecutedCommands, command =>
            command.ExecutedSql.Contains("INSERT INTO game_enrichment", StringComparison.Ordinal));
        Assert.Equal(unenrichedGameId, save.Parameters[CuratorSqlParameters.GameId].Value);
    }

    [Fact]
    public async Task EnrichDeltaAsync_WhenTwoEntitlementsCanonicalizeOntoOneGame_DoesNotFailOnTheRepeatedId()
    {
        // Arrange
        var sharedGameId = Guid.NewGuid();
        var harness = await HarnessAsync();
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable(sharedGameId, sharedGameId)));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var originalTitle = Generated.NewLongTitle();
        var candidates = new[]
        {
            Game(originalTitle, []),
            Game(Generated.WithAnEditionSuffix(originalTitle), []),
        };
        var gameIds = new[] { sharedGameId, sharedGameId };

        // Act
        var exception = await Record.ExceptionAsync(() => harness.Orchestrator.EnrichDeltaAsync(
            candidates, gameIds, [], new EnrichmentCredentials(), cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        const string reason =
            "Two entitlements canonicalizing onto one game repeat that game_id, and the unnested candidate "
            + "query repeats with it. Keying the needs by game id with ToDictionary throws on the duplicate, "
            + "where the ToHashSet it replaced tolerated it -- which turned an ordinary refresh into a failed "
            + "job for any library containing a re-release.";
        Assert.True(exception is null, reason + Environment.NewLine + exception?.Message);
    }

    [Fact]
    public async Task MatchTrophiesAsync_DelegatesToTrophyMatchService_SkippingTheStageWhenNoClientIsSupplied()
    {
        // Arrange
        var harness = await HarnessAsync();
        var game = Game(Generated.NewGameTitle(), []);

        // Act
        var result = await harness.Orchestrator.MatchTrophiesAsync(
            Generated.NewIdentitySub(),
            [game],
            [Generated.NewGameId()],
            new PsnTrophyClient(),
            null,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.AttemptedCount);
        Assert.Equal(0, harness.LibraryDb.ConnectionsCreated);
    }

    private static async Task<(
        LibraryBuildOrchestrator Orchestrator,
        FakeDbDataSource IngestionDb,
        FakeDbDataSource CatalogDb,
        FakeDbDataSource LibraryDb,
        FakeDbDataSource EnrichmentDb,
        PsnSession Session)> HarnessAsync(
        string? entitlementsBody = null)
    {
        var session = await ReadySessionAsync(
            StubHttpMessageHandler.Returns(Json(entitlementsBody ?? Entitlements())));
        var ingestionDb = new FakeDbDataSource();
        var catalogDb = new FakeDbDataSource();
        var libraryDb = new FakeDbDataSource();
        var enrichmentDb = new FakeDbDataSource();
        var enrichmentRepository = new EnrichmentRepository(enrichmentDb);
        var orchestrator = new LibraryBuildOrchestrator(
            new IngestionService(new PsnLibraryClient(), new EntitlementPullRepository(ingestionDb)),
            new CatalogRepository(catalogDb),
            new LibraryRepository(libraryDb),
            enrichmentRepository,
            new EnrichmentOrchestrationService(
                NotCalledRawgClient(),
                NotCalledOpenCriticClient(),
                new NotCalledCatalogClient(),
                enrichmentRepository,
                new OpenCriticCacheRepository(new FakeDbDataSource())),
            TelemetryHarness.Shared.Telemetry);
        return (orchestrator, ingestionDb, catalogDb, libraryDb, enrichmentDb, session);
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

    private static string Entitlements(params PsnEntitlementPayload[] entitlements) =>
        JsonSerializer.Serialize(
            new PsnEntitlementsResponse
            {
                TotalResults = entitlements.Length,
                Entitlements =
                    [.. entitlements.Select(entitlement => JsonSerializer.SerializeToElement(entitlement, PsnWireFormat))],
            },
            PsnWireFormat);

    private static string DownloadSizes(string entitlementId, long bytes) =>
        JsonSerializer.Serialize(
            new PsnCommerceEntitlementsResponse
            {
                TotalResults = 1,
                Entitlements =
                [
                    new PsnCommerceEntitlement
                    {
                        Id = entitlementId,
                        DrmDefinition = new PsnDrmDefinition
                        {
                            ContentType = PsnLibraryClient.GameContentType,
                            Contents = [new PsnDrmContent { ContentSize = bytes }],
                        },
                    },
                ],
            },
            PsnWireFormat);

    private static PsnEntitlementPayload OwnedGame(string title, string titleId) => new()
    {
        Id = Generated.NewEntitlementId(),
        IsGame = true,
        ActiveFlag = true,
        TitleMeta = new PsnTitleMeta { TitleId = titleId, Name = title },
        GameMeta = new PsnGameMeta { Name = title, PackageType = Ps5PackageType },
    };

    private static RawgClient NotCalledRawgClient() =>
        new(
            new HttpClient(StubHttpMessageHandler.Throws(NotCalled())),
            Generated.NewProviderBaseAddress());

    private static OpenCriticClient NotCalledOpenCriticClient() =>
        new(
            new HttpClient(StubHttpMessageHandler.Throws(NotCalled())),
            Generated.NewProviderBaseAddress());

    private static InvalidOperationException NotCalled() => new("This collaborator must not be called.");

    private static void SeedIngestion(FakeDbDataSource dataSource, int snapshotCount)
    {
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(Generated.NewEntitlementPullId()));
        for (var i = 0; i < snapshotCount; i++)
        {
            dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        }
    }

    private static void SeedEmptyCatalogRules(FakeDbDataSource dataSource)
    {
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
    }

    private static DataTable UnenrichedTable(params Guid[] gameIds)
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(bool),
            typeof(bool),
            typeof(bool));
        foreach (var gameId in gameIds)
        {
            table.Rows.Add(gameId, true, true, true);
        }

        return table;
    }

    private static CanonicalGame Game(string title, IReadOnlyList<string> conceptIds) =>
        new(
            title,
            NativePs5: true,
            Ps4Eligible: false,
            Generated.NewFranchiseName(),
            ProductId: Generated.NewProductId(),
            conceptIds,
            WinningEntitlementId: Generated.NewEntitlementId());

    private sealed class NotCalledCatalogClient : ICatalogClient
    {
        public Task<TitleConcept> TitleConceptAsync(
            PsnSession session,
            string titleId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TitleConcept>(NotCalled());
    }
}
