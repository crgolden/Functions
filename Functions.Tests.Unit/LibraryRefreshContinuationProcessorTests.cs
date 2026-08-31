namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Curator;
using Curator.Enrichment;
using Curator.Jobs;
using Curator.Library;
using Curator.OpenCritic;
using Curator.Psn;
using Curator.Rawg;
using TestSupport;

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
            (gameB, "Returnal", "PPSA02222_00", true), (gameA, "Bloodborne", "CUSA00900_00", false))));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        await LibraryRefreshContinuationProcessor.RunAsync(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            [gameA.ToString(), gameB.ToString()],
            harness.LibraryRepository,
            harness.EnrichmentService,
            harness.EnrichmentRepository,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            [],
            harness.Credentials,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var saves = harness.EnrichmentDb.ExecutedCommands
            .Where(command => command.ExecutedSql.Contains("INSERT INTO game_enrichment", StringComparison.Ordinal))
            .Select(command => command.ParameterValue<Guid>("@game_id"))
            .ToList();
        Assert.Equal([gameA, gameB], saves);
    }

    [Fact]
    public async Task RunAsync_UnionsTitlesWithTheExistingResultSummary_PreservingOrderAndDeduping()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var harness = await HarnessAsync(rawgHandler: null);
        harness.LibraryDb.Enqueue(FakeDbCommand.WithReader(ContinuationTable((gameId, "Bloodborne", "CUSA00900_00", false))));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var runId = Guid.NewGuid();
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithReader(RunTable(
            runId,
            JsonSerializer.Serialize(new LibraryRefreshResultSummary
            {
                RawgEnrichedTitles = ["Astro Bot"],
                OpenCriticEnrichedTitles = [],
                OpenCriticTopupIncomplete = true,
                RejectedProviders = [],
                UnavailableProviders = [],
            }))));

        // Act
        var result = await LibraryRefreshContinuationProcessor.RunAsync(
            runId.ToString(),
            Guid.NewGuid().ToString(),
            [gameId.ToString()],
            harness.LibraryRepository,
            harness.EnrichmentService,
            harness.EnrichmentRepository,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            [],
            harness.Credentials,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var summary = Assert.IsType<LibraryRefreshResultSummary>(result);
        Assert.Equal(["Astro Bot"], summary.RawgEnrichedTitles);
        Assert.True(summary.OpenCriticTopupIncomplete);
    }

    [Fact]
    public async Task RunAsync_MergesRateLimitedSummaryFromTheExistingRunAndRepublishesUsingTheMergedValues()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var harness = await HarnessAsync(handler);
        harness.LibraryDb.Enqueue(FakeDbCommand.WithReader(ContinuationTable((gameId, "Bloodborne", "CUSA00900_00", false))));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var runId = Guid.NewGuid();
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithReader(RunTable(
            runId,
            JsonSerializer.Serialize(new LibraryRefreshResultSummary
            {
                RawgEnrichedTitles = [],
                OpenCriticEnrichedTitles = [],
                OpenCriticTopupIncomplete = false,
                RejectedProviders = [EnrichmentProviderNames.OpenCritic],
                UnavailableProviders = [],
            }))));
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithScalarResult(3));

        // Act
        var exception = await Record.ExceptionAsync(() => LibraryRefreshContinuationProcessor.RunAsync(
            runId.ToString(),
            Guid.NewGuid().ToString(),
            [gameId.ToString()],
            harness.LibraryRepository,
            harness.EnrichmentService,
            harness.EnrichmentRepository,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            [],
            harness.Credentials,
            cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<ContinuationScheduledException>(exception);
        var markCommand = harness.JobRunsDb.ExecutedCommands[1];
        var summaryJson = markCommand.ParameterValue<string>("@result_summary");
        using var summary = JsonDocument.Parse(summaryJson);
        var rejectedProviders = summary.RootElement.GetProperty("rejected_providers").EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        Assert.Contains("opencritic", rejectedProviders);
        Assert.DoesNotContain("rawg", rejectedProviders);
        Assert.Equal("rawg", summary.RootElement.GetProperty("rate_limited_provider").GetString());
        Assert.Single(harness.PublishedMessages);
    }

    private static RawgClient NotCalledRawgClient() =>
        new(new HttpClient(StubHttpMessageHandler.Throws(NotCalled())), new Uri("https://rawg.invalid"));

    private static OpenCriticClient NotCalledOpenCriticClient() =>
        new(new HttpClient(StubHttpMessageHandler.Throws(NotCalled())), new Uri("https://opencritic.invalid"));

    private static InvalidOperationException NotCalled() => new("This collaborator must not be called.");

    private static DataTable ContinuationTable(params (Guid GameId, string Title, string? TitleId, bool NativePs5)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("product_id", typeof(string));
        table.Columns.Add("title_id", typeof(string));
        table.Columns.Add("native_ps5", typeof(bool));
        foreach (var row in rows)
        {
            table.Rows.Add(row.GameId, row.Title, DBNull.Value, row.TitleId, row.NativePs5);
        }

        return table;
    }

    private static DataTable RunTable(Guid runId, string resultSummaryJson)
    {
        var table = new DataTable();
        table.Columns.Add("run_id", typeof(Guid));
        table.Columns.Add("kind", typeof(string));
        table.Columns.Add("identity_sub", typeof(Guid));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("error", typeof(string));
        table.Columns.Add("seq", typeof(int));
        table.Columns.Add("result_summary", typeof(string));
        table.Rows.Add(runId, "library_refresh", DBNull.Value, "rate_limited", DBNull.Value, 1, resultSummaryJson);
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
            : new RawgClient(new HttpClient(rawgHandler), new Uri("https://rawg.invalid"));
        var enrichmentService = new EnrichmentOrchestrationService(
            rawgClient ?? NotCalledRawgClient(),
            NotCalledOpenCriticClient(),
            new NotCalledCatalogClient(),
            enrichmentRepository,
            new OpenCriticCacheRepository(new FakeDbDataSource()));
        var credentials = new EnrichmentCredentials
        {
            Rawg = rawgHandler is null ? null : new RawgCredential { ApiKey = Guid.NewGuid().ToString() },
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
