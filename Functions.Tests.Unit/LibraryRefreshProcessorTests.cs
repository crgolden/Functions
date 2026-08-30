namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Curator;
using Curator.Catalog;
using Curator.Enrichment;
using Curator.Jobs;
using Curator.Library;
using Curator.OpenCritic;
using Curator.Psn;
using Curator.Rawg;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class LibraryRefreshProcessorTests
{
    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task RunAsync_ReturnsASucceededSummary_WhenNothingWasRateLimitedOrRejected()
    {
        // Arrange
        var harness = await HarnessAsync(rawgHandler: null);
        SeedIngestAndCanonicalize(harness.IngestionDb, harness.CatalogDb);
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(Guid.NewGuid()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable()));

        // Act
        var result = await LibraryRefreshProcessor.RunAsync(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            harness.Orchestrator,
            harness.EnrichmentService,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            new PsnTrophyClient(),
            harness.Session,
            null,
            [],
            harness.Credentials,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var summary = Assert.IsType<LibraryRefreshResultSummary>(result);
        Assert.Empty(summary.RawgEnrichedTitles);
        Assert.Empty(summary.RejectedProviders);
    }

    [Fact]
    public async Task RunAsync_MarksRateLimitedAndPublishesAContinuationThenThrows_WhenRawgIsRateLimited()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var harness = await HarnessAsync(handler);
        SeedIngestAndCanonicalize(harness.IngestionDb, harness.CatalogDb);
        var gameId = Guid.NewGuid();
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(gameId));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable(gameId)));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithScalarResult(2));
        var runId = Guid.NewGuid().ToString();

        // Act
        var exception = await Record.ExceptionAsync(() => LibraryRefreshProcessor.RunAsync(
            runId,
            Guid.NewGuid().ToString(),
            harness.Orchestrator,
            harness.EnrichmentService,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            new PsnTrophyClient(),
            harness.Session,
            null,
            [],
            harness.Credentials,
            cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var rateLimited = Assert.IsType<ContinuationScheduledException>(exception);
        Assert.Equal("rawg", rateLimited.Provider);
        var markRateLimited = harness.JobRunsDb.ExecutedCommands[0];
        Assert.Contains("rate_limited", markRateLimited.CapturedCommandText, StringComparison.Ordinal);
        var published = Assert.Single(harness.PublishedMessages);
        Assert.Equal(runId, GetJsonProperty(published, "run_id"));
        Assert.Equal("rawg", GetJsonProperty(published, "provider"));
        Assert.True(published.ScheduledEnqueueTime > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RunAsync_MarksTheRawgKeyRejected_WhenRawgRejectsIt()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var harness = await HarnessAsync(handler);
        SeedIngestAndCanonicalize(harness.IngestionDb, harness.CatalogDb);
        var gameId = Guid.NewGuid();
        var identitySub = Guid.NewGuid().ToString();
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(gameId));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable(gameId)));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        await LibraryRefreshProcessor.RunAsync(
            Guid.NewGuid().ToString(),
            identitySub,
            harness.Orchestrator,
            harness.EnrichmentService,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            new PsnTrophyClient(),
            harness.Session,
            null,
            [],
            harness.Credentials,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var markRejected = Assert.Single(harness.EnrichmentKeysDb.ExecutedCommands);
        Assert.Contains("rawg_key_rejected_at", markRejected.CapturedCommandText, StringComparison.Ordinal);
        Assert.Equal(Guid.Parse(identitySub), markRejected.Parameters["@identity_sub"].Value);
        Assert.DoesNotContain(
            harness.EnrichmentDb.ExecutedCommands,
            command => command.ExecutedSql.Contains("rawg_key_rejected_at", StringComparison.Ordinal));

        var audit = Assert.Single(harness.AuditDb.ExecutedCommands);
        Assert.Contains("INSERT INTO account_action_log", audit.CapturedCommandText, StringComparison.Ordinal);
        Assert.Equal(Guid.Parse(identitySub), audit.Parameters["@identity_sub"].Value);
        Assert.Equal(AccountActionLogRepository.EnrichmentKeyRejected, audit.Parameters["@action"].Value);
        Assert.Equal(EnrichmentProviderNames.Rawg, audit.Parameters["@detail"].Value);
    }

    [Fact]
    public async Task RunAsync_StillMarksTheKeyRejected_WhenTheAuditLogWriteFails()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var harness = await HarnessAsync(handler);
        SeedIngestAndCanonicalize(harness.IngestionDb, harness.CatalogDb);
        var gameId = Guid.NewGuid();
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(gameId));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable(gameId)));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.AuditDb.Enqueue(FakeDbCommand.ThatThrowsOnExecute());

        // Act
        var exception = await Record.ExceptionAsync(() => LibraryRefreshProcessor.RunAsync(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            harness.Orchestrator,
            harness.EnrichmentService,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            new PsnTrophyClient(),
            harness.Session,
            null,
            [],
            harness.Credentials,
            cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception);
        var markRejected = Assert.Single(harness.EnrichmentKeysDb.ExecutedCommands);
        Assert.Contains("rawg_key_rejected_at", markRejected.CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MarksTheRunContinuingAndQueuesTheRemainderImmediately_WhenTheTimeBudgetIsSpent()
    {
        // Arrange
        var harness = await HarnessAsync(rawgHandler: null);
        SeedIngestAndCanonicalize(harness.IngestionDb, harness.CatalogDb);
        var gameId = Guid.NewGuid();
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(gameId));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable(gameId)));
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithScalarResult(2));
        var runId = Guid.NewGuid().ToString();

        // Act
        var exception = await Record.ExceptionAsync(() => LibraryRefreshProcessor.RunAsync(
            runId,
            Guid.NewGuid().ToString(),
            harness.Orchestrator,
            harness.EnrichmentService,
            harness.EnrichmentKeysRepository,
            harness.AuditRepository,
            harness.JobRuns,
            harness.Publisher,
            new PsnTrophyClient(),
            harness.Session,
            null,
            [],
            harness.Credentials,
            new JobTimeBudget(TimeSpan.Zero),
            TestContext.Current.CancellationToken));

        // Assert
        var continuation = Assert.IsType<ContinuationScheduledException>(exception);
        Assert.Equal(JobStoppedReasons.TimeBudget, continuation.StoppedReason);
        Assert.Null(continuation.Provider);
        Assert.Equal(0, continuation.RetryAfterSeconds);

        var markContinuing = harness.JobRunsDb.ExecutedCommands[0];
        var summary = markContinuing.ParameterValue<string>("@result_summary");
        Assert.Contains(@"""stopped_reason"":""time_budget""", summary, StringComparison.Ordinal);
        Assert.Contains(@"""retry_after_seconds"":0", summary, StringComparison.Ordinal);

        var published = Assert.Single(harness.PublishedMessages);
        Assert.Equal(runId, GetJsonProperty(published, "run_id"));
        Assert.Null(GetJsonProperty(published, "provider"));
        Assert.Equal([gameId.ToString()], RemainingGameIds(published));
    }

    private static async Task<PsnSession> ReadySessionAsync(StubHttpMessageHandler handler) =>
        await PsnSession.RestoreAsync(
            null,
            SeededStore(),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

    private static IPsnTokenStore SeededStore()
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
        new(new HttpClient(StubHttpMessageHandler.Throws(NotCalled())), new Uri("https://rawg.invalid"));

    private static OpenCriticClient NotCalledOpenCriticClient() =>
        new(new HttpClient(StubHttpMessageHandler.Throws(NotCalled())), new Uri("https://opencritic.invalid"));

    private static Exception NotCalled() => new InvalidOperationException("This collaborator must not be called.");

    private static IReadOnlyList<string> RemainingGameIds(ServiceBusMessage message)
    {
        using var document = JsonDocument.Parse(message.Body);
        return document.RootElement
            .GetProperty("remaining_game_ids")
            .EnumerateArray()
            .Select(id => Assert.IsType<string>(id.GetString()))
            .ToList();
    }

    private static string? GetJsonProperty(ServiceBusMessage message, string property)
    {
        using var document = JsonDocument.Parse(message.Body);
        return document.RootElement.GetProperty(property).GetString();
    }

    private static void SeedIngestAndCanonicalize(FakeDbDataSource ingestionDb, FakeDbDataSource catalogDb)
    {
        ingestionDb.Enqueue(FakeDbCommand.WithScalarResult(Guid.NewGuid()));
        ingestionDb.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
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

    private static async Task<(
        LibraryBuildOrchestrator Orchestrator,
        EnrichmentOrchestrationService EnrichmentService,
        EnrichmentKeysRepository EnrichmentKeysRepository,
        AccountActionLogRepository AuditRepository,
        JobRunsRepository JobRuns,
        LibraryRefreshQueuePublisher Publisher,
        FakeDbDataSource IngestionDb,
        FakeDbDataSource CatalogDb,
        FakeDbDataSource EnrichmentDb,
        FakeDbDataSource JobRunsDb,
        FakeDbDataSource EnrichmentKeysDb,
        FakeDbDataSource AuditDb,
        List<ServiceBusMessage> PublishedMessages,
        PsnSession Session,
        EnrichmentCredentials Credentials)> HarnessAsync(StubHttpMessageHandler? rawgHandler)
    {
        var session = await ReadySessionAsync(
            StubHttpMessageHandler.Returns(Json(Entitlements(OwnedGame("Bloodborne", "CUSA00900_00")))));
        var credentials = new EnrichmentCredentials
        {
            Rawg = rawgHandler is null ? null : new RawgCredential { ApiKey = Guid.NewGuid().ToString() },
        };
        var ingestionDb = new FakeDbDataSource();
        var catalogDb = new FakeDbDataSource();
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
        var orchestrator = new LibraryBuildOrchestrator(
            new IngestionService(new PsnLibraryClient(), new EntitlementPullRepository(ingestionDb)),
            new CatalogRepository(catalogDb),
            new LibraryRepository(libraryDb),
            enrichmentRepository,
            enrichmentService);
        var (factory, sent) = FakeServiceBus.Create();
        return (
            orchestrator,
            enrichmentService,
            new EnrichmentKeysRepository(enrichmentKeysDb),
            new AccountActionLogRepository(auditDb),
            new JobRunsRepository(jobRunsDb),
            new LibraryRefreshQueuePublisher(factory),
            ingestionDb,
            catalogDb,
            enrichmentDb,
            jobRunsDb,
            enrichmentKeysDb,
            auditDb,
            sent,
            session,
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
