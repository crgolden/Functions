namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Functions.Curator;
using Functions.Curator.Catalog;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.Library;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;
using Functions.Tests.Unit.TestSupport;

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
        var resolvedGameId = Guid.NewGuid();
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(resolvedGameId));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable()));

        // Act
        var result = await LibraryRefreshProcessor.RunAsync(
            Generated.NewRunId(),
            Generated.NewIdentitySub(),
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
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithScalarResult(Generated.NewJobRunSeq()));
        var runId = Guid.NewGuid();

        // Act
        var exception = await Record.ExceptionAsync(() => LibraryRefreshProcessor.RunAsync(
            runId,
            Generated.NewIdentitySub(),
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
        Assert.Equal(EnrichmentProviderNames.Rawg, rateLimited.Provider);
        var markRateLimited = harness.JobRunsDb.ExecutedCommands[0];
        Assert.Contains(JobRunStatuses.RateLimited, markRateLimited.CapturedCommandText, StringComparison.Ordinal);
        var published = Assert.Single(harness.PublishedMessages);
        var continuation = ContinuationOf(published);
        Assert.Equal(runId, continuation.RunId);
        Assert.Equal(EnrichmentProviderNames.Rawg, continuation.Provider);
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
        var identitySub = Generated.NewIdentitySub();
        harness.CatalogDb.Enqueue(FakeDbCommand.WithScalarResult(gameId));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(UnenrichedTable(gameId)));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        harness.EnrichmentDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));

        // Act
        await LibraryRefreshProcessor.RunAsync(
            Generated.NewRunId(),
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
        Assert.Equal(identitySub, markRejected.Parameters[CuratorSqlParameters.IdentitySub].Value);
        Assert.DoesNotContain(
            harness.EnrichmentDb.ExecutedCommands,
            command => command.ExecutedSql.Contains("rawg_key_rejected_at", StringComparison.Ordinal));

        var audit = Assert.Single(harness.AuditDb.ExecutedCommands);
        Assert.Contains("INSERT INTO account_action_log", audit.CapturedCommandText, StringComparison.Ordinal);
        Assert.Equal(identitySub, audit.Parameters[CuratorSqlParameters.IdentitySub].Value);
        Assert.Equal(AccountActionLogRepository.EnrichmentKeyRejected, audit.Parameters[CuratorSqlParameters.Action].Value);
        Assert.Equal(EnrichmentProviderNames.Rawg, audit.Parameters[CuratorSqlParameters.Detail].Value);
    }

    [Fact]
    public async Task RunAsync_FailsTheRunButKeepsTheKeyMarkedRejected_WhenTheAuditLogWriteFails()
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
            Generated.NewRunId(),
            Generated.NewIdentitySub(),
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
        Assert.IsType<FakeDbException>(exception);
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
        harness.JobRunsDb.Enqueue(FakeDbCommand.WithScalarResult(Generated.NewJobRunSeq()));
        var runId = Guid.NewGuid();

        // Act
        var exception = await Record.ExceptionAsync(() => LibraryRefreshProcessor.RunAsync(
            runId,
            Generated.NewIdentitySub(),
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
        var scheduled = Assert.IsType<ContinuationScheduledException>(exception);
        Assert.Equal(JobStoppedReasons.TimeBudget, scheduled.StoppedReason);
        Assert.Null(scheduled.Provider);
        Assert.Equal(0, scheduled.RetryAfterSeconds);

        var markContinuing = harness.JobRunsDb.ExecutedCommands[0];
        var summary = Assert.IsType<LibraryRefreshContinuationSummary>(JsonSerializer.Deserialize<LibraryRefreshContinuationSummary>(
            Assert.IsType<string>(markContinuing.Parameters[CuratorSqlParameters.ResultSummary].Value)));
        Assert.Equal(JobStoppedReasons.TimeBudget, summary.StoppedReason);
        Assert.Equal(0, summary.RetryAfterSeconds);

        var continuation = ContinuationOf(Assert.Single(harness.PublishedMessages));
        Assert.Equal(runId, continuation.RunId);
        Assert.Null(continuation.Provider);
        Assert.Equal([gameId], continuation.RemainingGameIds);
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
        var lifetime = TimeSpan.FromHours(1);
        store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = Guid.NewGuid().ToString(),
                ExpiresIn = (int)lifetime.TotalSeconds,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds(),
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

    private static string NoDownloadSizes() =>
        JsonSerializer.Serialize(new PsnCommerceEntitlementsResponse { TotalResults = 0 }, PsnWireFormat);

    private static PsnEntitlementPayload OwnedGame(string title, string titleId) => new()
    {
        Id = Generated.NewEntitlementId(),
        IsGame = true,
        ActiveFlag = true,
        TitleMeta = new PsnTitleMeta { TitleId = titleId, Name = title },
        GameMeta = new PsnGameMeta { Name = title, PackageType = ContentKinds.Ps5GamePackageType },
    };

    private static RawgClient NotCalledRawgClient() =>
        new(new HttpClient(StubHttpMessageHandler.Throws(NotCalled())), Generated.NewProviderBaseAddress());

    private static OpenCriticClient NotCalledOpenCriticClient() =>
        new(new HttpClient(StubHttpMessageHandler.Throws(NotCalled())), Generated.NewProviderBaseAddress());

    private static InvalidOperationException NotCalled() => new("This collaborator must not be called.");

    private static LibraryRefreshContinuationMessage ContinuationOf(ServiceBusMessage message) =>
        Assert.IsType<LibraryRefreshContinuationMessage>(message.Body.ToObjectFromJson<LibraryRefreshContinuationMessage>());

    private static void SeedIngestAndCanonicalize(FakeDbDataSource ingestionDb, FakeDbDataSource catalogDb)
    {
        var pullId = Guid.NewGuid();
        ingestionDb.Enqueue(FakeDbCommand.WithScalarResult(pullId));
        ingestionDb.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        catalogDb.Enqueue(FakeDbCommand.WithReader(new DataTable()));
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
            StubHttpMessageHandler.Sequence(
                Json(Entitlements(OwnedGame(Generated.NewGameTitle(), Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix)))),
                Json(NoDownloadSizes())));
        var credentials = new EnrichmentCredentials
        {
            Rawg = rawgHandler is null ? null : new RawgCredential { ApiKey = Generated.NewRawgApiKey() },
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
            : new RawgClient(new HttpClient(rawgHandler), Generated.NewProviderBaseAddress());
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
            enrichmentService,
            TelemetryHarness.Shared.Telemetry);
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
