namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Functions.Curator;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.Library;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;
using Functions.Tests.Unit.TestSupport;

[Trait("Category", "Unit")]
public sealed class LibraryRefreshContinuationProcessorTests
{
    [Fact]
    public async Task RunAsync_EnrichesEachRequestedGameInTheRequestedOrder_NotDatabaseRowOrder()
    {
        // Arrange
        var gameA = Guid.NewGuid();
        var gameB = Guid.NewGuid();
        var harness = await HarnessAsync(rawgHandler: null);
        harness.LibraryDb.Enqueue(FakeDbCommand.WithReader(ContinuationTable(
            (gameB, Generated.NewGameTitle(), Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix), true),
            (gameA, Generated.NewGameTitle(), Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), false))));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        await LibraryRefreshContinuationProcessor.RunAsync(
            Generated.NewRunId(),
            Generated.NewIdentitySub(),
            [gameA, gameB],
            harness.LibraryRepository,
            harness.EnrichmentService,
            harness.EnrichmentRepository,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            [],
            harness.Credentials,
            TelemetryHarness.Shared.Telemetry,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var saves = harness.EnrichmentDb.ExecutedCommands
            .Where(command => command.ExecutedSql.Contains("INSERT INTO game_enrichment", StringComparison.Ordinal))
            .Select(command => Assert.IsType<Guid>(command.Parameters[CuratorSqlParameters.GameId].Value))
            .ToList();
        Assert.Equal([gameA, gameB], saves);
    }

    [Fact]
    public async Task RunAsync_UnionsTitlesWithTheExistingResultSummary_PreservingOrderAndDeduping()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var harness = await HarnessAsync(rawgHandler: null);
        var alreadyEnrichedTitle = Generated.NewGameTitle();
        harness.LibraryDb.Enqueue(FakeDbCommand.WithReader(
            ContinuationTable((gameId, Generated.NewGameTitle(), Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), false))));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var runId = Guid.NewGuid();
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithReader(RunTable(
            runId,
            JsonSerializer.Serialize(new LibraryRefreshResultSummary
            {
                RawgEnrichedTitles = [alreadyEnrichedTitle],
                OpenCriticEnrichedTitles = [],
                OpenCriticTopupIncomplete = true,
                RejectedProviders = [],
                UnavailableProviders = [],
            }))));

        // Act
        var result = await LibraryRefreshContinuationProcessor.RunAsync(
            runId,
            Generated.NewIdentitySub(),
            [gameId],
            harness.LibraryRepository,
            harness.EnrichmentService,
            harness.EnrichmentRepository,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            [],
            harness.Credentials,
            TelemetryHarness.Shared.Telemetry,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var summary = Assert.IsType<LibraryRefreshResultSummary>(result);
        Assert.Equal([alreadyEnrichedTitle], summary.RawgEnrichedTitles);
        Assert.True(summary.OpenCriticTopupIncomplete);
    }

    [Fact]
    public async Task RunAsync_MergesRateLimitedSummaryFromTheExistingRunAndRepublishesUsingTheMergedValues()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var harness = await HarnessAsync(handler);
        harness.LibraryDb.Enqueue(FakeDbCommand.WithReader(
            ContinuationTable((gameId, Generated.NewGameTitle(), Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), false))));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var runId = Guid.NewGuid();
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithReader(RunTable(
            runId,
            JsonSerializer.Serialize(new LibraryRefreshResultSummary
            {
                RawgEnrichedTitles = [],
                OpenCriticEnrichedTitles = [],
                RejectedProviders = [EnrichmentProviderNames.OpenCritic],
                UnavailableProviders = [],
            }))));
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithScalarResult(Generated.NewJobRunSeq()));

        // Act
        var exception = await Record.ExceptionAsync(() => LibraryRefreshContinuationProcessor.RunAsync(
            runId,
            Generated.NewIdentitySub(),
            [gameId],
            harness.LibraryRepository,
            harness.EnrichmentService,
            harness.EnrichmentRepository,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            [],
            harness.Credentials,
            TelemetryHarness.Shared.Telemetry,
            cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<ContinuationScheduledException>(exception);
        var markCommand = harness.JobRunsDb.ExecutedCommands[1];
        var summaryJson = Assert.IsType<string>(markCommand.Parameters[CuratorSqlParameters.ResultSummary].Value);
        var summary = Assert.IsType<LibraryRefreshContinuationSummary>(
            JsonSerializer.Deserialize<LibraryRefreshContinuationSummary>(summaryJson));
        Assert.Contains(EnrichmentProviderNames.OpenCritic, summary.RejectedProviders);
        Assert.DoesNotContain(EnrichmentProviderNames.Rawg, summary.RejectedProviders);
        Assert.Equal(EnrichmentProviderNames.Rawg, summary.RateLimitedProvider);
        Assert.Single(harness.PublishedMessages);
    }

    private static RawgClient NotCalledRawgClient() =>
        new(new HttpClient(StubHttpMessageHandler.Throws(NotCalled())), Generated.NewProviderBaseAddress());

    private static OpenCriticClient NotCalledOpenCriticClient() =>
        new(new HttpClient(StubHttpMessageHandler.Throws(NotCalled())), Generated.NewProviderBaseAddress());

    private static InvalidOperationException NotCalled() => new("This collaborator must not be called.");

    private static DataTable ContinuationTable(params (Guid GameId, string Title, string? TitleId, bool NativePs5)[] rows)
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(bool));
        foreach (var row in rows)
        {
            table.Rows.Add(row.GameId, row.Title, DBNull.Value, row.TitleId, row.NativePs5);
        }

        return table;
    }

    private static DataTable RunTable(Guid runId, string resultSummaryJson)
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(int),
            typeof(string));
        table.Rows.Add(
            runId,
            JobRunKinds.LibraryRefresh,
            DBNull.Value,
            JobRunStatuses.RateLimited,
            DBNull.Value,
            1,
            resultSummaryJson);
        return table;
    }

    private static async Task<(
        LibraryRepository LibraryRepository,
        EnrichmentOrchestrationService EnrichmentService,
        EnrichmentRepository EnrichmentRepository,
        EnrichmentKeysRepository EnrichmentKeysRepository,
        AccountActionLogRepository AuditRepository,
        JobRunsRepository JobRuns,
        LibraryRefreshQueuePublisher Publisher,
        FakeDbDataSource LibraryDb,
        FakeDbDataSource EnrichmentDb,
        FakeDbDataSource JobRunsDb,
        List<ServiceBusMessage> PublishedMessages,
        EnrichmentCredentials Credentials)> HarnessAsync(StubHttpMessageHandler? rawgHandler)
    {
        var libraryDb = new FakeDbDataSource();
        var enrichmentDb = new FakeDbDataSource();
        var jobRunsDb = new FakeDbDataSource();
        var enrichmentKeysDb = new FakeDbDataSource();
        var auditDb = new FakeDbDataSource();
        var enrichmentRepository = new EnrichmentRepository(enrichmentDb);
        var rawgClient = rawgHandler is null
            ? null
            : new RawgClient(new HttpClient(rawgHandler), Generated.NewProviderBaseAddress());
        var enrichmentService = new EnrichmentOrchestrationService(
            rawgClient ?? NotCalledRawgClient(),
            NotCalledOpenCriticClient(),
            new NotCalledCatalogClient(),
            enrichmentRepository,
            new OpenCriticCacheRepository(new FakeDbDataSource()));
        var credentials = new EnrichmentCredentials
        {
            Rawg = rawgHandler is null ? null : new RawgCredential { ApiKey = Generated.NewRawgApiKey() },
        };
        var (factory, sent) = FakeServiceBus.Create();
        return (
            new LibraryRepository(libraryDb),
            enrichmentService,
            enrichmentRepository,
            new EnrichmentKeysRepository(enrichmentKeysDb),
            new AccountActionLogRepository(auditDb),
            new JobRunsRepository(jobRunsDb),
            new LibraryRefreshQueuePublisher(factory),
            libraryDb,
            enrichmentDb,
            jobRunsDb,
            sent,
            credentials);
    }

    private sealed class NotCalledCatalogClient : ICatalogClient
    {
        public Task<TitleConcept> TitleConceptAsync(
            PsnSession session,
            string titleId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TitleConcept>(NotCalled());
    }
}
