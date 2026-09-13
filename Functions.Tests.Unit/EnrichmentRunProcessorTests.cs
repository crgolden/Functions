namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
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

    private const int NoRows = 0;
    private const int TheOneGameUnderTest = 1;
    private const int CommandsForASkippedRun = 5;
    private const int CommandsForARunThatQueriesCandidates = 6;

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
        Assert.Equal(CommandsForASkippedRun, dataSource.ExecutedCommands.Count);
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
        var franchiseKeyword = TestValues.NewTokenFromFirstHalfOfAlphabet(6);
        var titleMatchingTheRule = $"{franchiseKeyword} {TestValues.NewTokenFromFirstHalfOfAlphabet(7)}";
        var titleSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(11);
        var staleFingerprint = TestValues.NewFingerprint();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(FranchiseRulesTable(franchiseKeyword)));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(staleFingerprint));
        dataSource.Enqueue(FakeDbCommand.WithReader(
            GamesTable(titleMatchingTheRule, titleSharingNoCharactersWithIt)));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(EmptyRuleListFingerprint));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        var summary = await RunAsync(dataSource);

        // Assert
        Assert.Equal(EnrichmentRunProcessor.Ran, summary.FranchiseReclassification.Status);
        Assert.Equal(TheOneGameUnderTest, summary.FranchiseReclassification.UpdatedCount);
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
        var unenrichedGameId = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTable(unenrichedGameId)));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        var summary = await RunAsync(dataSource);

        // Assert
        Assert.Equal(NoRows, summary.Enrichment.AttemptedCount);
        Assert.Equal(NoRows, summary.Enrichment.EnrichedCount);
        Assert.Equal(NoRows, summary.Enrichment.RemainingCount);
        Assert.Equal(CommandsForARunThatQueriesCandidates, dataSource.ExecutedCommands.Count);
    }

    [Fact]
    public async Task RunAsync_AsksOnlyTheProvidersThatTitleStillNeeds_LeavingTheSatisfiedOnesAlone()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        var gameId = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTable(gameId)));
        dataSource.Enqueue(FakeDbCommand.WithReader(NeedsTable(gameId, rawg: false, openCritic: true, psn: false)));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var exception = await Record.ExceptionAsync(() => RunAsync(
            dataSource,
            credentials: new EnrichmentCredentials
            {
                Rawg = new RawgCredential { ApiKey = TestValues.NewRawgApiKey() },
                Psn = NewRotation(),
            }));

        // Assert
        const string reason =
            "The RAWG and PS Store clients here throw the moment they are used, so reaching either one fails "
            + "this outright. Only OpenCritic is outstanding for this title, so only OpenCritic may be asked: "
            + "selecting a game because ONE provider is missing must not re-query the two that already "
            + "answered, which is the whole point of tracking the flags per provider.";
        Assert.True(exception is null, reason + " Instead: " + exception?.Message);
    }

    [Fact]
    public async Task RunAsync_BuildsItsWorklistFromTheSameCandidateQueryALibraryRefreshUses()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        var unenrichedGameId = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTable(unenrichedGameId)));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        await RunAsync(dataSource);

        // Assert
        const string reason =
            "The catalog run used to union a second 'never asked of RAWG' query onto the candidate list, which "
            + "existed only because the shared predicate selected on row existence and could not see a game "
            + "RAWG had never answered for. Now that the predicate tests every success flag, the two paths "
            + "share one query and the union would be a second, divergent definition of the same thing.";
        var candidateQueries = dataSource.ExecutedCommands
            .Where(command => command.CapturedCommandText?.Contains(
                "NOT game_enrichment.rawg_enriched", StringComparison.Ordinal) == true)
            .ToList();
        var candidateQuery = Assert.Single(candidateQueries);
        Assert.DoesNotContain(
            dataSource.ExecutedCommands,
            command => command.CapturedCommandText?.Contains("attempted_at IS NULL", StringComparison.Ordinal) == true);
        var selectsOnPsnSuccess = candidateQuery.CapturedCommandText?.Contains(
            "NOT game_enrichment.psn_enriched", StringComparison.Ordinal) == true;
        Assert.True(selectsOnPsnSuccess, reason);
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
        Assert.Equal(TheOneGameUnderTest, summary.Enrichment.EnrichedCount);
        Assert.Equal(TheOneGameUnderTest, summary.Enrichment.AttemptedCount);
        Assert.Equal(NoRows, summary.Enrichment.RemainingCount);
        Assert.Null(summary.Enrichment.StoppedProvider);
        Assert.Equal(NoRows, summary.Enrichment.RawgEnrichedCount);
        Assert.Equal(NoRows, summary.Enrichment.OpenCriticEnrichedCount);
    }

    [Fact]
    public async Task RunAsync_ReportsPerProviderEnrichedCountsDerivedFromWhichProvidersActuallyContributed()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        var gameId = Guid.NewGuid();
        var gameTitle = TestValues.NewGameTitle();
        var openCriticScore = TestValues.NewOpenCriticScore();
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTableNamed(gameId, gameTitle)));
        dataSource.Enqueue(FakeDbCommand.WithReader(GameIdTable(gameId)));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(RawgCacheRow());
        dataSource.Enqueue(OpenCriticCacheRow(gameTitle, openCriticScore));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var enrichmentService = NewService(repository, dataSource, rawgClient: NewRawgClient());

        // Act
        var summary = await RunAsync(
            dataSource,
            enrichmentService: enrichmentService,
            credentials: new EnrichmentCredentials
            {
                Rawg = new RawgCredential { ApiKey = TestValues.NewRawgApiKey() },
            });

        // Assert
        Assert.Equal(TheOneGameUnderTest, summary.Enrichment.RawgEnrichedCount);
        Assert.Equal(TheOneGameUnderTest, summary.Enrichment.OpenCriticEnrichedCount);
        Assert.Equal(NoRows, summary.Enrichment.PsnEnrichedCount);
    }

    [Fact]
    public async Task RunAsync_CountsAPsnResolvedTitle_EvenWhenTheConceptCarriesNoStarRating()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueSkippedReclassificationPasses(dataSource);
        var gameId = Guid.NewGuid();
        dataSource.Enqueue(FakeDbCommand.WithReader(CatalogGamesTable(gameId, NewTitleId())));
        dataSource.Enqueue(FakeDbCommand.WithReader(GameIdTable(gameId)));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var enrichmentService = NewService(repository, dataSource, catalogClient: new StubCatalogClient());

        // Act
        var summary = await RunAsync(
            dataSource,
            enrichmentService: enrichmentService,
            credentials: new EnrichmentCredentials { Psn = NewRotation() });

        // Assert
        Assert.Equal(TheOneGameUnderTest, summary.Enrichment.EnrichedCount);
        Assert.Equal(TheOneGameUnderTest, summary.Enrichment.PsnEnrichedCount);
        Assert.Equal(NoRows, summary.Enrichment.RawgEnrichedCount);
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
            TestValues.NewProviderBaseAddress());
        var enrichmentService = NewService(repository, dataSource, rawgClient: rawgClient);

        // Act
        var summary = await RunAsync(
            dataSource,
            enrichmentService: enrichmentService,
            credentials: new EnrichmentCredentials
            {
                Rawg = new RawgCredential { ApiKey = TestValues.NewRawgApiKey() },
            });

        // Assert
        Assert.Equal([EnrichmentProviderNames.Rawg], summary.Enrichment.RejectedProviders);
        Assert.Equal(EnrichmentProviderNames.Rawg, summary.Enrichment.StoppedProvider);
        Assert.Equal(JobStoppedReasons.AuthError, summary.Enrichment.StoppedReason);
        Assert.Equal(NoRows, summary.Enrichment.EnrichedCount);
        Assert.Equal(TheOneGameUnderTest, summary.Enrichment.RemainingCount);
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
        Assert.True(root.TryGetProperty(
            EnrichmentRunSummaryFields.OpenCriticCacheRefresh, out var openCritic));
        Assert.True(root.TryGetProperty(
            EnrichmentRunSummaryFields.FranchiseReclassification, out var franchise));
        Assert.True(root.TryGetProperty(
            EnrichmentRunSummaryFields.TierReclassification, out var tier));
        Assert.True(root.TryGetProperty(EnrichmentRunSummaryFields.Enrichment, out var enrichment));
        Assert.Equal(
            EnrichmentRunProcessor.NotConfigured,
            openCritic.GetProperty(EnrichmentRunSummaryFields.Status).GetString());
        Assert.Equal(
            EnrichmentRunProcessor.SkippedUnchanged,
            franchise.GetProperty(EnrichmentRunSummaryFields.Status).GetString());
        Assert.Equal(
            EnrichmentRunProcessor.SkippedUnchanged,
            tier.GetProperty(EnrichmentRunSummaryFields.Status).GetString());
        Assert.Equal(NoRows, enrichment.GetProperty(EnrichmentRunSummaryFields.EnrichedCount).GetInt32());
        Assert.Equal(NoRows, enrichment.GetProperty(EnrichmentRunSummaryFields.RemainingCount).GetInt32());
        Assert.Equal(
            NoRows, enrichment.GetProperty(EnrichmentRunSummaryFields.RawgEnrichedCount).GetInt32());
        Assert.Equal(
            NoRows,
            enrichment.GetProperty(EnrichmentRunSummaryFields.OpenCriticEnrichedCount).GetInt32());
        Assert.Equal(
            NoRows, enrichment.GetProperty(EnrichmentRunSummaryFields.PsnEnrichedCount).GetInt32());
        Assert.Equal(
            JsonValueKind.Null,
            enrichment.GetProperty(EnrichmentRunSummaryFields.StoppedProvider).ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            enrichment.GetProperty(EnrichmentRunSummaryFields.StoppedReason).ValueKind);
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
        var openCritic = document.RootElement.GetProperty(
            EnrichmentRunSummaryFields.OpenCriticCacheRefresh);
        Assert.False(openCritic.TryGetProperty(EnrichmentRunSummaryFields.GamesFetched, out _));
        Assert.False(document.RootElement
            .GetProperty(EnrichmentRunSummaryFields.FranchiseReclassification)
            .TryGetProperty(EnrichmentRunSummaryFields.UpdatedCount, out _));
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
            TestValues.NewProviderBaseAddress());

    private static OpenCriticClient NewOpenCriticClient() =>
        new(
            new HttpClient(StubHttpMessageHandler.Throws(new InvalidOperationException("not called"))),
            TestValues.NewProviderBaseAddress());

    private static PsnSessionRotation NewRotation() =>
        new([new PsnSession(null, null, NullPsnRateLimiter.Unthrottled)]);

    private static OpenCriticAdminRefreshService NewAdminRefresher(
        FakeDbDataSource dataSource, HttpStatusCode statusCode)
    {
        var handler = StubHttpMessageHandler.Always(
            () => JsonResponse.WithStatus(statusCode, JsonResponse.EmptyArray));
        var client = new OpenCriticClient(
            new HttpClient(handler),
            TestValues.NewProviderBaseAddress());
        return new OpenCriticAdminRefreshService(
            new OpenCriticCacheRepository(dataSource),
            client,
            [new OpenCriticCredential { RapidApiKey = TestValues.NewRapidApiKey() }]);
    }

    private static void QueueSkippedReclassificationPasses(FakeDbDataSource dataSource)
    {
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(EmptyRuleListFingerprint));
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(EmptyRuleListFingerprint));
    }

    private static DataTable FranchiseRulesTable(string pattern)
    {
        var table = new DataTable();
        table.Columns.Add("rule_id", typeof(Guid));
        table.Columns.Add("pattern", typeof(string));
        table.Columns.Add("franchise", typeof(string));
        table.Columns.Add("priority", typeof(int));
        var ruleId = Guid.NewGuid();
        table.Rows.Add(ruleId, pattern, TestValues.NewFranchiseName(), TestValues.NewRulePriority());
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
            var gameId = Guid.NewGuid();
            table.Rows.Add(gameId, title, DBNull.Value);
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
            table.Rows.Add(gameId, TestValues.NewGameTitle(), DBNull.Value);
        }

        return table;
    }

    private static DataTable CatalogGamesTable(Guid gameId, string titleId)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("title_id", typeof(string));
        table.Rows.Add(gameId, TestValues.NewGameTitle(), titleId);
        return table;
    }

    private static DataTable CatalogGamesTableNamed(Guid gameId, string canonicalTitle)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("title_id", typeof(string));
        table.Rows.Add(gameId, canonicalTitle, DBNull.Value);
        return table;
    }

    private static string NewTitleId() => TestValues.NewTitleId();

    private static DataTable NeedsTable(Guid gameId, bool rawg, bool openCritic, bool psn)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("needs_rawg", typeof(bool));
        table.Columns.Add("needs_opencritic", typeof(bool));
        table.Columns.Add("needs_psn", typeof(bool));
        table.Rows.Add(gameId, rawg, openCritic, psn);
        return table;
    }

    private static DataTable GameIdTable(params Guid[] gameIds)
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

    private static FakeDbCommand RawgCacheRow()
    {
        var table = new DataTable();
        table.Columns.Add("normalized_title", typeof(string));
        table.Columns.Add("rawg_game_id", typeof(int));
        table.Columns.Add("raw", typeof(string));
        table.Rows.Add(
            TestValues.NewNormalizedTitle(), TestValues.NewRawgGameId(), JsonResponse.EmptyObject);
        return FakeDbCommand.WithReader(table);
    }

    private static FakeDbCommand OpenCriticCacheRow(string name, double topCriticScore)
    {
        var table = new DataTable();
        table.Columns.Add("oc_game_id", typeof(int));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("top_critic_score", typeof(double));
        table.Columns.Add("tier", typeof(string));
        table.Columns.Add("percent_recommended", typeof(double));
        table.Rows.Add(Random.Shared.Next(1, 1_000_000), name, topCriticScore, TestValues.NewOpenCriticTier(), TestValues.NewOpenCriticScore());
        return FakeDbCommand.WithReader(table);
    }

    private sealed class StubCatalogClient : ICatalogClient
    {
        public Task<TitleConcept> TitleConceptAsync(
            PsnSession session,
            string titleId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TitleConcept
            {
                ConceptId = Random.Shared.Next(1, 1_000_000).ToString(CultureInfo.InvariantCulture),
            });
    }
}
