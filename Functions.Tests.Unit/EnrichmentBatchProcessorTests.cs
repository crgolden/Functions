namespace Functions.Tests.Unit;

using System.Data;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Curator.Enrichment;
using Curator.Jobs;
using Curator.OpenCritic;
using Curator.Psn;
using Curator.Rawg;
using Microsoft.Extensions.Time.Testing;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentBatchProcessorTests
{
    private static readonly JsonSerializerOptions OpenCriticWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task EnrichGamesAsync_WhenRawgReturns429_DisablesRawgAndContinuesTheBatchAgainstOtherProviders()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueTwoGameRunWhereRawgFailsOnTheFirstAttempt(dataSource);
        var repository = new EnrichmentRepository(dataSource);
        var openCriticCacheRepository = new OpenCriticCacheRepository(dataSource);
        var rawgClient = NewRawgClient(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var (service, credentials) = NewService(repository, openCriticCacheRepository, rawgClient: rawgClient);
        var games = TwoGames();

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service, repository, games, [], credentials, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.EnrichedCount);
        Assert.Equal(EnrichmentProvider.Rawg, result.RateLimitedProvider);
        Assert.Empty(result.RejectedProviders);
        Assert.Equal([games[0].GameId, games[1].GameId], result.RemainingGameIds);
        Assert.Equal(5, dataSource.ExecutedCommands.Count);
    }

    [Fact]
    public async Task EnrichGamesAsync_WhenRawgRejectsTheKey_DisablesRawgImmediatelyRatherThanRetryingEveryGame()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueTwoGameRunWhereRawgFailsOnTheFirstAttempt(dataSource);
        var repository = new EnrichmentRepository(dataSource);
        var openCriticCacheRepository = new OpenCriticCacheRepository(dataSource);
        var rawgClient = NewRawgClient(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var (service, credentials) = NewService(repository, openCriticCacheRepository, rawgClient: rawgClient);
        var games = TwoGames();

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service, repository, games, [], credentials, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.EnrichedCount);
        Assert.Equal([EnrichmentProvider.Rawg], result.RejectedProviders);
        Assert.Null(result.RateLimitedProvider);
        Assert.Equal(5, dataSource.ExecutedCommands.Count);
    }

    [Fact]
    public async Task EnrichGamesAsync_WhenAskedToStopOnFirstProviderFailure_LeavesEveryGameUnenrichedForALaterRun()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueTwoGameRunWhereRawgFailsOnTheFirstAttempt(dataSource);
        var repository = new EnrichmentRepository(dataSource);
        var openCriticCacheRepository = new OpenCriticCacheRepository(dataSource);
        var rawgClient = NewRawgClient(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var (service, credentials) = NewService(repository, openCriticCacheRepository, rawgClient: rawgClient);
        var games = TwoGames();

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service,
            repository,
            games,
            [],
            credentials,
            stopOnFirstProviderFailure: true,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.EnrichedCount);
        Assert.Equal([EnrichmentProvider.Rawg], result.RejectedProviders);
        Assert.Equal([games[0].GameId, games[1].GameId], result.RemainingGameIds);
    }

    [Fact]
    public async Task EnrichGamesAsync_WhenOpenCriticReturns429DuringTheBuiltInTopup_DisablesOpenCriticAndContinuesTheBatch()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueTwoGameRunWhereOpenCriticFailsDuringTopup(dataSource);
        var repository = new EnrichmentRepository(dataSource);
        var openCriticCacheRepository = new OpenCriticCacheRepository(dataSource);
        var openCriticClient = NewOpenCriticClient(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var (service, credentials) = NewService(
            repository, openCriticCacheRepository, openCriticClient: openCriticClient);
        var games = TwoGames();

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service, repository, games, [], credentials, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.EnrichedCount);
        Assert.Equal(EnrichmentProvider.OpenCritic, result.RateLimitedProvider);
        Assert.Empty(result.RejectedProviders);
        Assert.Equal([games[0].GameId, games[1].GameId], result.RemainingGameIds);
        Assert.Equal(5, dataSource.ExecutedCommands.Count);
    }

    [Fact]
    public async Task EnrichGamesAsync_WhenOpenCriticRejectsTheKeyDuringTheBuiltInTopup_DisablesOpenCriticImmediately()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueTwoGameRunWhereOpenCriticFailsDuringTopup(dataSource);
        var repository = new EnrichmentRepository(dataSource);
        var openCriticCacheRepository = new OpenCriticCacheRepository(dataSource);
        var openCriticClient = NewOpenCriticClient(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var (service, credentials) = NewService(
            repository, openCriticCacheRepository, openCriticClient: openCriticClient);
        var games = TwoGames();

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service, repository, games, [], credentials, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.EnrichedCount);
        Assert.Equal([EnrichmentProvider.OpenCritic], result.RejectedProviders);
        Assert.Null(result.RateLimitedProvider);
        Assert.Equal(5, dataSource.ExecutedCommands.Count);
    }

    [Fact]
    public async Task EnrichGamesAsync_WithNoGames_SavesNothingAndReturnsAnEmptyResult()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(ActiveGenresReader());
        var repository = new EnrichmentRepository(dataSource);
        var (service, credentials) = NewService(repository, new OpenCriticCacheRepository(dataSource));

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service, repository, [], [], credentials, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.EnrichedCount);
        Assert.Null(result.RateLimitedProvider);
        Assert.Empty(result.RemainingGameIds);
    }

    [Fact]
    public async Task EnrichGamesAsync_WhenTheTimeBudgetIsAlreadySpent_EnrichesNothingAndHandsTheWholeBatchToTheContinuation()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(ActiveGenresReader());
        var repository = new EnrichmentRepository(dataSource);
        var (service, credentials) = NewService(repository, new OpenCriticCacheRepository(dataSource));
        var games = TwoGames();

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service,
            repository,
            games,
            [],
            credentials,
            timeBudget: new JobTimeBudget(TimeSpan.Zero),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, result.EnrichedCount);
        Assert.Equal(JobStoppedReasons.TimeBudget, result.StoppedReason);
        Assert.Equal([games[0].GameId, games[1].GameId], result.RemainingGameIds);
        Assert.Null(result.RateLimitedProvider);
    }

    [Fact]
    public async Task EnrichGamesAsync_WhenTheTimeBudgetRunsOutMidBatch_KeepsWhatItEnrichedAndResumesAtTheNextGame()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(ActiveGenresReader());
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(SaveEnrichmentCommand());
        var repository = new EnrichmentRepository(dataSource);
        var (service, credentials) = NewService(repository, new OpenCriticCacheRepository(dataSource));
        var games = TwoGames();
        var timeProvider = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMinutes(15) };

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service,
            repository,
            games,
            [],
            credentials,
            timeBudget: new JobTimeBudget(TimeSpan.FromMinutes(20), timeProvider),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, result.EnrichedCount);
        Assert.Equal(JobStoppedReasons.TimeBudget, result.StoppedReason);
        Assert.Equal([games[1].GameId], result.RemainingGameIds);
    }

    [Fact]
    public async Task EnrichGamesAsync_WhenTheBatchFinishesInsideTheBudget_ReportsNoStoppedReason()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        QueueTwoGameRunWhereOpenCriticFailsDuringTopup(dataSource);
        var repository = new EnrichmentRepository(dataSource);
        var (service, credentials) = NewService(repository, new OpenCriticCacheRepository(dataSource));
        var games = TwoGames();

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service,
            repository,
            games,
            [],
            credentials,
            timeBudget: new JobTimeBudget(TimeSpan.FromHours(1)),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.EnrichedCount);
        Assert.Null(result.StoppedReason);
        Assert.Empty(result.RemainingGameIds);
    }

    [Fact]
    public async Task EnrichGamesAsync_ReadsTheOpenCriticCacheOnce_NotOncePerGame()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(ActiveGenresReader());
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(SaveEnrichmentCommand());
        dataSource.Enqueue(SaveEnrichmentCommand());
        var repository = new EnrichmentRepository(dataSource);
        var (service, credentials) = NewService(repository, new OpenCriticCacheRepository(dataSource));

        // Act
        var result = await EnrichmentBatchProcessor.EnrichGamesAsync(
            service, repository, TwoGames(), [], credentials, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.EnrichedCount);
        Assert.Single(OpenCriticCacheReads(dataSource));
    }

    [Fact]
    public async Task EnrichGamesAsync_RereadsTheOpenCriticCache_AfterATopupWritesNewGamesToIt()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(ActiveGenresReader());
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(OpenCriticCursorReader());
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(SaveEnrichmentCommand());
        dataSource.Enqueue(SaveEnrichmentCommand());
        var repository = new EnrichmentRepository(dataSource);
        var openCriticClient = NewOpenCriticClient(
            StubHttpMessageHandler.Always(() => OpenCriticGamesPage()));
        var (service, credentials) = NewService(
            repository, new OpenCriticCacheRepository(dataSource), openCriticClient: openCriticClient);

        // Act
        await EnrichmentBatchProcessor.EnrichGamesAsync(
            service, repository, TwoGames(), [], credentials, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, OpenCriticCacheReads(dataSource).Count);
    }

    private static List<FakeDbCommand> OpenCriticCacheReads(FakeDbDataSource dataSource) =>
        [.. dataSource.ExecutedCommands.Where(command =>
            command.CapturedCommandText?.Contains("FROM opencritic_cache", StringComparison.Ordinal) == true)];

    private static HttpResponseMessage OpenCriticGamesPage()
    {
        var page = JsonSerializer.Serialize(
            new[]
            {
                new OpenCriticGameEntry
                {
                    Id = 12550,
                    Name = "Dead Cells",
                    TopCriticScore = 89.0,
                    Tier = TestValues.NewOpenCriticTier(),
                    PercentRecommended = 92.0,
                },
            },
            OpenCriticWireFormat);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(page, Encoding.UTF8, "application/json"),
        };
    }

    private static (EnrichmentOrchestrationService Service, EnrichmentCredentials Credentials) NewService(
        EnrichmentRepository repository,
        OpenCriticCacheRepository openCriticCacheRepository,
        IRawgClient? rawgClient = null,
        IOpenCriticClient? openCriticClient = null)
    {
        var service = new EnrichmentOrchestrationService(
            rawgClient ?? NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            openCriticClient ?? NewOpenCriticClient(StubHttpMessageHandler.Throws(NotCalled())),
            new UnusedCatalogClient(),
            repository,
            openCriticCacheRepository);
        var credentials = new EnrichmentCredentials
        {
            Rawg = rawgClient is null ? null : new RawgCredential { ApiKey = Guid.NewGuid().ToString() },
            OpenCritic = openCriticClient is null
                ? null
                : new OpenCriticCredential { RapidApiKey = Guid.NewGuid().ToString() },
        };
        return (service, credentials);
    }

    private static InvalidOperationException NotCalled() => new("This collaborator must not be called.");

    private static List<EnrichmentCandidate> TwoGames() =>
    [
        new(Guid.NewGuid().ToString(), "Game A", null, null, true),
        new(Guid.NewGuid().ToString(), "Game B", null, null, true),
    ];

    private static void QueueTwoGameRunWhereRawgFailsOnTheFirstAttempt(FakeDbDataSource dataSource)
    {
        dataSource.Enqueue(ActiveGenresReader());
        dataSource.Enqueue(RawgCacheMissReader());
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(SaveEnrichmentCommand());
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(SaveEnrichmentCommand());
    }

    private static void QueueTwoGameRunWhereOpenCriticFailsDuringTopup(FakeDbDataSource dataSource)
    {
        dataSource.Enqueue(ActiveGenresReader());
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(OpenCriticCursorReader());
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(SaveEnrichmentCommand());
        dataSource.Enqueue(OpenCriticMatchReader());
        dataSource.Enqueue(SaveEnrichmentCommand());
    }

    private static RawgClient NewRawgClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new Uri("https://api.rawg.io/api/"));

    private static OpenCriticClient NewOpenCriticClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new Uri("https://opencritic-api.p.rapidapi.com/"));

    private static FakeDbCommand ActiveGenresReader() => FakeDbCommand.WithReader(new DataTable());

    private static FakeDbCommand RawgCacheMissReader() => FakeDbCommand.WithReader(new DataTable());

    private static FakeDbCommand OpenCriticMatchReader() => FakeDbCommand.WithReader(new DataTable());

    private static FakeDbCommand OpenCriticCursorReader() => FakeDbCommand.WithScalarResult(0);

    private static FakeDbCommand SaveEnrichmentCommand() => FakeDbCommand.WithNonQueryResult(1);

    private sealed class UnusedCatalogClient : ICatalogClient
    {
        public Task<TitleConcept> TitleConceptAsync(
            PsnSession session,
            string titleId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TitleConcept>(NotCalled());
    }
}
