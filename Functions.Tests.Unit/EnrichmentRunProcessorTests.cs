namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Curator.Catalog;
using Curator.Enrichment;
using Curator.Jobs;
using Curator.OpenCritic;
using Curator.Psn;
using Curator.Rawg;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentRunProcessorTests
{
    private const string EmptyRuleListFingerprint =
        "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945";

    [Fact]
    public async Task RunAsync_WithUnchangedRuleFingerprints_SkipsBothReclassificationPasses()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        var summary = await RunAsync(dataSource);

        // Assert
        Assert.Equal(EnrichmentRunProcessor.SkippedUnchanged, summary.FranchiseReclassification.Status);
        Assert.Equal(EnrichmentRunProcessor.SkippedUnchanged, summary.TierReclassification.Status);
        Assert.Equal(6, dataSource.ExecutedCommands.Count);
    }

    [Fact]
    public async Task RunAsync_WhenNeitherPassHasEverRun_ReclassifiesRatherThanSkipping()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();

        // Act
        var summary = await RunAsync(dataSource);

        // Assert
        Assert.Equal(EnrichmentRunProcessor.Ran, summary.FranchiseReclassification.Status);
        Assert.Equal(EnrichmentRunProcessor.Ran, summary.TierReclassification.Status);
    }

    [Fact]
    public async Task RunAsync_WithChangedFranchiseRules_ReclassifiesAndReportsTheUpdatedCount()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(FranchiseRulesTable()));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult("a-stale-fingerprint"));
        dataSource.Enqueue(FakeDbCommand.WithReader(GamesTable("Halo Infinite", "Tetris Effect")));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(EmptyRuleListFingerprint));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        var summary = await RunAsync(dataSource);

        // Assert
        Assert.Equal(EnrichmentRunProcessor.Ran, summary.FranchiseReclassification.Status);
        Assert.Equal(1, summary.FranchiseReclassification.UpdatedCount);
    }

    [Fact]
    public async Task RunAsync_WithNoOpenCriticKeys_ReportsTheSweepAsNotConfigured()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();

        // Act
        var summary = await RunAsync(dataSource);

        // Assert
        Assert.Equal(EnrichmentRunProcessor.NotConfigured, summary.OpenCriticCacheRefresh.Status);
        Assert.Null(summary.OpenCriticCacheRefresh.GamesFetched);
    }

    [Fact]
    public async Task RunAsync_WhenTheOpenCriticSweepIsRateLimited_ReportsItInsteadOfFailingTheRun()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var refresher = NewAdminRefresher(dataSource, HttpStatusCode.TooManyRequests);

        // Act
        var summary = await RunAsync(dataSource, openCriticAdminRefresh: refresher);

        // Assert
        Assert.Equal(JobStoppedReasons.RateLimited, summary.OpenCriticCacheRefresh.Status);
        Assert.NotNull(summary.OpenCriticCacheRefresh.RetryAfterSeconds);
        Assert.Equal(EnrichmentRunProcessor.Ran, summary.FranchiseReclassification.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheOpenCriticSweepKeyIsRejected_ReportsAuthErrorWithTheProviderDetail()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var refresher = NewAdminRefresher(dataSource, HttpStatusCode.Forbidden);

        // Act
        var summary = await RunAsync(dataSource, openCriticAdminRefresh: refresher);

        // Assert
        Assert.Equal(JobStoppedReasons.AuthError, summary.OpenCriticCacheRefresh.Status);
        Assert.False(string.IsNullOrWhiteSpace(summary.OpenCriticCacheRefresh.Detail));
    }

    [Fact]
    public async Task RunAsync_WhenOpenCriticAdminKeysAreConfigured_ReportsOpenCriticAsAnAvailableProvider()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var refresher = NewAdminRefresher(dataSource, HttpStatusCode.OK);

        // Act
        var summary = await RunAsync(dataSource, openCriticAdminRefresh: refresher);

        // Assert
        Assert.Equal(EnrichmentRunProcessor.Ok, summary.OpenCriticCacheRefresh.Status);
        Assert.Equal(EnrichmentRunProcessor.Ok, summary.Enrichment.Providers[EnrichmentProviderNames.OpenCritic]);
    }

    [Fact]
    public async Task RunAsync_WithAnAdminPsnCatalogClient_ReportsPsnAsConfigured()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var repository = new EnrichmentRepository(dataSource);
        var enrichmentService = NewService(repository, dataSource, catalogClient: new StubCatalogClient());

        // Act
        var summary = await RunAsync(
            dataSource,
            enrichmentService: enrichmentService,
            credentials: new EnrichmentCredentials { Psn = NewRotation() });

        // Assert
        Assert.Equal(EnrichmentRunProcessor.Ok, summary.Enrichment.Providers[EnrichmentProviderNames.Psn]);
    }

    [Fact]
    public async Task RunAsync_WithNoUnenrichedGames_ReportsZeroCountsWithoutBuildingAWorklist()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTable(Guid.NewGuid())));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        var summary = await RunAsync(dataSource);

        // Assert
        Assert.Equal(0, summary.Enrichment.AttemptedCount);
        Assert.Equal(0, summary.Enrichment.EnrichedCount);
        Assert.Equal(0, summary.Enrichment.RemainingCount);
        Assert.Equal(7, dataSource.ExecutedCommands.Count);
    }

    [Fact]
    public async Task RunAsync_AsksTheCatalogForGamesRawgWasNeverReachedFor_NotOnlyGamesWithNoEnrichmentRowAtAll()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTable(Guid.NewGuid())));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        await RunAsync(dataSource);

        // Assert
        Assert.Contains(
            dataSource.ExecutedCommands,
            command => command.CapturedCommandText?.Contains(
                "rawg_attempted_at IS NULL", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RunAsync_EnrichesOnlyTheGamesWithNoEnrichmentRow()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        var enriched = Guid.NewGuid();
        var unenriched = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTable(enriched, unenriched)));
        dataSource.Enqueue(FakeDbCommand.WithReader(GameIdTable(unenriched)));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var summary = await RunAsync(dataSource);

        // Assert
        Assert.Equal(1, summary.Enrichment.EnrichedCount);
        Assert.Equal(1, summary.Enrichment.AttemptedCount);
        Assert.Equal(0, summary.Enrichment.RemainingCount);
        Assert.Null(summary.Enrichment.StoppedProvider);
    }

    [Fact]
    public async Task RunAsync_StopsTheCatalogPassAndNamesTheProvider_WhenRawgRejectsTheKey()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        var gameId = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTable(gameId)));
        dataSource.Enqueue(FakeDbCommand.WithReader(GameIdTable(gameId)));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var rawgClient = new RawgClient(
            new HttpClient(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized))),
            new Uri("https://api.rawg.io/api/"));
        var enrichmentService = NewService(repository, dataSource, rawgClient: rawgClient);

        // Act
        var summary = await RunAsync(
            dataSource,
            enrichmentService: enrichmentService,
            credentials: new EnrichmentCredentials
            {
                Rawg = new RawgCredential { ApiKey = Guid.NewGuid().ToString() },
            });

        // Assert
        Assert.Equal([EnrichmentProviderNames.Rawg], summary.Enrichment.RejectedProviders);
        Assert.Equal(EnrichmentProviderNames.Rawg, summary.Enrichment.StoppedProvider);
        Assert.Equal(JobStoppedReasons.AuthError, summary.Enrichment.StoppedReason);
        Assert.Equal(0, summary.Enrichment.EnrichedCount);
        Assert.Equal(1, summary.Enrichment.RemainingCount);
    }

    [Fact]
    public async Task RunAsync_SerializesTheFourPassKeysAndTheEnrichmentCountsTheAdminPageReads()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        var json = JsonSerializer.Serialize(await RunAsync(dataSource));

        // Assert
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("opencritic_cache_refresh", out var openCritic));
        Assert.True(root.TryGetProperty("franchise_reclassification", out var franchise));
        Assert.True(root.TryGetProperty("tier_reclassification", out var tier));
        Assert.True(root.TryGetProperty("enrichment", out var enrichment));
        Assert.Equal(EnrichmentRunProcessor.NotConfigured, openCritic.GetProperty("status").GetString());
        Assert.Equal(EnrichmentRunProcessor.SkippedUnchanged, franchise.GetProperty("status").GetString());
        Assert.Equal(EnrichmentRunProcessor.SkippedUnchanged, tier.GetProperty("status").GetString());
        Assert.Equal(0, enrichment.GetProperty("enriched_count").GetInt32());
        Assert.Equal(0, enrichment.GetProperty("remaining_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, enrichment.GetProperty("stopped_provider").ValueKind);
        Assert.Equal(JsonValueKind.Null, enrichment.GetProperty("stopped_reason").ValueKind);
    }

    [Fact]
    public async Task RunAsync_OmitsThePassFieldsThatDoNotApplyRatherThanEmittingThemAsNull()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        var json = JsonSerializer.Serialize(await RunAsync(dataSource));

        // Assert
        using var document = JsonDocument.Parse(json);
        var openCritic = document.RootElement.GetProperty("opencritic_cache_refresh");
        Assert.False(openCritic.TryGetProperty("games_fetched", out _));
        Assert.False(document.RootElement.GetProperty("franchise_reclassification").TryGetProperty("updated_count", out _));
    }

    private static Task<EnrichmentRunSummary> RunAsync(
        FakeDbDataSource dataSource,
        OpenCriticAdminRefreshService? openCriticAdminRefresh = null,
        EnrichmentOrchestrationService? enrichmentService = null,
        EnrichmentCredentials? credentials = null,
        JobTimeBudget? timeBudget = null)
    {
        var repository = new EnrichmentRepository(dataSource);
        var service = enrichmentService ?? NewService(repository, dataSource);
        return EnrichmentRunProcessor.RunAsync(
            openCriticAdminRefresh,
            service,
            credentials ?? new EnrichmentCredentials(),
            new CatalogRepository(dataSource),
            repository,
            timeBudget,
            TestContext.Current.CancellationToken);
    }

    private static EnrichmentOrchestrationService NewService(
        EnrichmentRepository repository,
        FakeDbDataSource dataSource,
        IRawgClient? rawgClient = null,
        ICatalogClient? catalogClient = null) =>
        new(
            rawgClient ?? NewRawgClient(),
            NewOpenCriticClient(),
            catalogClient ?? new StubCatalogClient(),
            repository,
            new OpenCriticCacheRepository(dataSource));

    private static RawgClient NewRawgClient() =>
        new(
            new HttpClient(StubHttpMessageHandler.Throws(new InvalidOperationException("not called"))),
            new Uri("https://api.rawg.io/api/"));

    private static OpenCriticClient NewOpenCriticClient() =>
        new(
            new HttpClient(StubHttpMessageHandler.Throws(new InvalidOperationException("not called"))),
            new Uri("https://opencritic-api.p.rapidapi.com/"));

    private static PsnSessionRotation NewRotation() =>
        new([new PsnSession(null, null, NullPsnRateLimiter.Unthrottled)]);

    private static OpenCriticAdminRefreshService NewAdminRefresher(
        FakeDbDataSource dataSource, HttpStatusCode statusCode)
    {
        var handler = StubHttpMessageHandler.Always(() => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        var client = new OpenCriticClient(
            new HttpClient(handler),
            new Uri("https://opencritic-api.p.rapidapi.com/"));
        return new OpenCriticAdminRefreshService(
            new OpenCriticCacheRepository(dataSource),
            client,
            [new OpenCriticCredential { RapidApiKey = Guid.NewGuid().ToString() }]);
    }

    private static void QueueSkippedReclassificationPasses(FakeDbDataSource dataSource)
    {
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(EmptyRuleListFingerprint));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(EmptyRuleListFingerprint));
    }

    private static DataTable FranchiseRulesTable()
    {
        var table = new DataTable();
        table.Columns.Add("rule_id", typeof(Guid));
        table.Columns.Add("pattern", typeof(string));
        table.Columns.Add("franchise", typeof(string));
        table.Columns.Add("priority", typeof(int));
        table.Rows.Add(Guid.NewGuid(), "halo", "Halo", 1);
        return table;
    }

    private static DataTable GamesTable(params string[] titles)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("franchise", typeof(string));
        foreach (var title in titles)
        {
            table.Rows.Add(Guid.NewGuid(), title, DBNull.Value);
        }

        return table;
    }

    private static DataTable CatalogGamesTable(params Guid[] gameIds)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("title_id", typeof(string));
        foreach (var gameId in gameIds)
        {
            table.Rows.Add(gameId, "Game " + gameId, DBNull.Value);
        }

        return table;
    }

    private static DataTable GameIdTable(params Guid[] gameIds)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        foreach (var gameId in gameIds)
        {
            table.Rows.Add(gameId);
        }

        return table;
    }

    private sealed class StubCatalogClient : ICatalogClient
    {
        public Task<TitleConcept> TitleConceptAsync(
            PsnSession session,
            string titleId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TitleConcept());
    }
}
