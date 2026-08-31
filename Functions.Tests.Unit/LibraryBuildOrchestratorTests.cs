namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.Catalog;
using Curator.Enrichment;
using Curator.Library;
using Curator.OpenCritic;
using Curator.Psn;
using Curator.Rawg;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class LibraryBuildOrchestratorTests
{
    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task CanonicalizeAsync_IngestsThenAppliesCatalogRulesToProduceCanonicalGames()
    {
        // Arrange
        var harness = await HarnessAsync(Entitlements(OwnedGame("Bloodborne", "CUSA00900_00")));
        SeedIngestion(harness.IngestionDb, snapshotCount: 1);
        SeedEmptyCatalogRules(harness.CatalogDb);

        // Act
        var games = await harness.Orchestrator.CanonicalizeAsync(
            Guid.NewGuid().ToString(), harness.Session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(games);
        Assert.Equal("Bloodborne", game.CanonicalTitle);
        Assert.True(game.NativePs5);
        Assert.Equal("CUSA00900_00", game.WinningTitleId);
    }

    [Fact]
    public async Task CanonicalizeAsync_DropsATitleExcludedByAMediaAppRule()
    {
        // Arrange
        var harness = await HarnessAsync(Entitlements(OwnedGame("Netflix", "CUSA00900_00")));
        SeedIngestion(harness.IngestionDb, snapshotCount: 1);
        var exclusionTable = new DataTable();
        exclusionTable.Columns.Add("rule_id", typeof(Guid));
        exclusionTable.Columns.Add("rule_type", typeof(string));
        exclusionTable.Columns.Add("pattern", typeof(string));
        exclusionTable.Rows.Add(Guid.NewGuid(), "media_app", "Netflix");
        harness.CatalogDb.Enqueue(FakeDbCommand.WithReader(exclusionTable));
        SeedEmptyCatalogRules(harness.CatalogDb, skipExclusion: true);

        // Act
        var games = await harness.Orchestrator.CanonicalizeAsync(
            Guid.NewGuid().ToString(), harness.Session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public async Task PersistAndLinkAsync_UpsertsEachGameAndLinksItToTheIdentitysLibrary()
    {
        // Arrange
        var identitySub = Guid.NewGuid();
        var existingGameId = Guid.NewGuid();
        var harness = await HarnessAsync();
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(existingGameId));
        var game = Game("Bloodborne", ["c1"]);

        // Act
        var gameIds = await harness.Orchestrator.PersistAndLinkAsync(
            identitySub.ToString(), [game], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([existingGameId.ToString()], gameIds);
        var insert = harness.LibraryDb.ExecutedCommands[0];
        Assert.Equal(identitySub, insert.Parameters["@identity_sub"].Value);
        var batch = Assert.IsType<string>(insert.Parameters["@batch"].Value);
        var row = Assert.Single(JsonDocument.Parse(batch).RootElement.EnumerateArray());
        Assert.Equal(existingGameId, row.GetProperty("game_id").GetGuid());
    }

    [Fact]
    public async Task EnrichDeltaAsync_RejectsMismatchedGamesAndIds()
    {
        // Arrange
        var harness = await HarnessAsync();
        var game = Game("Bloodborne", []);

        // Act
        var exception = await Record.ExceptionAsync(() => harness.Orchestrator.EnrichDeltaAsync(
            [game],
            ["game-1", "game-2"],
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
            Game("Bloodborne", []),
            Game("Returnal", []),
        };
        var gameIds = new[] { enrichedGameId.ToString(), unenrichedGameId.ToString() };

        // Act
        var result = await harness.Orchestrator.EnrichDeltaAsync(
            candidates, gameIds, [], new EnrichmentCredentials(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.EnrichedCount);
        var save = Assert.Single(harness.EnrichmentDb.ExecutedCommands, command =>
            command.ExecutedSql.Contains("INSERT INTO game_enrichment", StringComparison.Ordinal));
        Assert.Equal(unenrichedGameId, save.Parameters["@game_id"].Value);
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
        var candidates = new[]
        {
            Game("Bloodborne", []),
            Game("Bloodborne (Game of the Year Edition)", []),
        };
        var gameIds = new[] { sharedGameId.ToString(), sharedGameId.ToString() };

        // Act
        var exception = await Record.ExceptionAsync(() => harness.Orchestrator.EnrichDeltaAsync(
            candidates, gameIds, [], new EnrichmentCredentials(), cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        const string reason =
            "Two entitlements canonicalizing onto one game repeat that game_id, and the unnested candidate "
            + "query repeats with it. Keying the needs by game id with ToDictionary throws on the duplicate, "
            + "where the ToHashSet it replaced tolerated it -- which turned an ordinary refresh into a failed "
            + "job for any library containing a re-release.";
        Assert.True(exception is null, reason + " Instead: " + exception?.Message);
    }

    [Fact]
    public async Task MatchTrophiesAsync_DelegatesToTrophyMatchService_SkippingTheStageWhenNoClientIsSupplied()
    {
        // Arrange
        var harness = await HarnessAsync();
        var game = Game("Bloodborne", []);

        // Act
        var result = await harness.Orchestrator.MatchTrophiesAsync(
            Guid.NewGuid().ToString(),
            [game],
            [Guid.NewGuid().ToString()],
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
                new OpenCriticCacheRepository(new FakeDbDataSource())));
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
                AccessToken = Guid.NewGuid().ToString(),
                ExpiresIn = 3600,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Entitlements(params PsnEntitlementPayload[] entitlements) =>
        JsonSerializer.Serialize(
            new PsnEntitlementsResponse
            {
                TotalResults = entitlements.Length,
                Entitlements =
                    [.. entitlements.Select(entitlement => JsonSerializer.SerializeToElement(entitlement, PsnWireFormat))],
            },
            PsnWireFormat);

    private static PsnEntitlementPayload OwnedGame(string title, string titleId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        IsGame = true,
        ActiveFlag = true,
        TitleMeta = new PsnTitleMeta { TitleId = titleId, Name = title },
        GameMeta = new PsnGameMeta { Name = title, PackageType = "PSGD" },
    };

    private static RawgClient NotCalledRawgClient() =>
        new(
            new HttpClient(StubHttpMessageHandler.Throws(NotCalled())),
            new Uri("https://api.rawg.io/api/"));

    private static OpenCriticClient NotCalledOpenCriticClient() =>
        new(
            new HttpClient(StubHttpMessageHandler.Throws(NotCalled())),
            new Uri("https://opencritic-api.p.rapidapi.com/"));

    private static InvalidOperationException NotCalled() => new("This collaborator must not be called.");

    private static void SeedIngestion(FakeDbDataSource dataSource, int snapshotCount)
    {
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(Guid.NewGuid()));
        for (var i = 0; i < snapshotCount; i++)
        {
            dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        }
    }

    private static void SeedEmptyCatalogRules(FakeDbDataSource dataSource, bool skipExclusion = false)
    {
        if (!skipExclusion)
        {
            dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        }

        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
    }

    private static DataTable UnenrichedTable(params Guid[] gameIds)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("needs_rawg", typeof(bool));
        table.Columns.Add("needs_opencritic", typeof(bool));
        table.Columns.Add("needs_psn", typeof(bool));
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
            "None",
            ProductId: "prod-1",
            conceptIds,
            WinningEntitlementId: "e1");

    private sealed class NotCalledCatalogClient : ICatalogClient
    {
        public Task<TitleConcept> TitleConceptAsync(
            PsnSession session,
            string titleId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TitleConcept>(NotCalled());
    }
}
