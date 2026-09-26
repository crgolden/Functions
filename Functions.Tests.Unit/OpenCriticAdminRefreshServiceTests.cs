namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Functions.Curator;
using Functions.Curator.Enrichment;
using Functions.Curator.OpenCritic;
using Functions.Tests.Unit.TestSupport;
using static Functions.Tests.Unit.OpenCriticAdminRefreshServiceFixtureConstants;

[Trait("Category", "Unit")]
public sealed class OpenCriticAdminRefreshServiceTests
{
    private static readonly string KeyPrefix = $"{Generated.LowercaseToken(6)}-";

    private static readonly JsonSerializerOptions OpenCriticWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public void Constructor_WithAnEmptyClientList_Throws()
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());

        // Act
        var exception = Record.Exception(() => new OpenCriticAdminRefreshService(
            repository, NewClient(StubHttpMessageHandler.Throws(NotCalled())), []));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void AdminRefreshMaxPages_Is20()
    {
        // Act
        var cap = OpenCriticAdminRefreshService.AdminRefreshMaxPages;

        // Assert
        Assert.Equal(OpenCriticAdminRefreshService.AdminRefreshMaxPages, cap);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RefreshCacheAsync_RotatesToTheNextKey_OnARotatingStatusCode(HttpStatusCode statusCode)
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());
        var rejectedHandler = StubHttpMessageHandler.Returns(new HttpResponseMessage(statusCode));
        var acceptedHandler = StubHttpMessageHandler.Returns(JsonResponse.OkEmptyArray());
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(rejectedHandler, acceptedHandler),
            Keys(TwoKeys));

        // Act
        var outcome = await refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, outcome.GamesFetched);
        Assert.Single(acceptedHandler.Requests);
    }

    [Fact]
    public async Task RefreshCacheAsync_DoesNotRotate_OnANonRotatingApiFailure()
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());
        var failingHandler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var neverCalledHandler = StubHttpMessageHandler.Throws(NotCalled());
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(failingHandler, neverCalledHandler),
            Keys(TwoKeys));

        // Act
        var outcome = await refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, outcome.GamesFetched);
        Assert.Empty(neverCalledHandler.Requests);
    }

    [Fact]
    public async Task RefreshCacheAsync_DoesNotRotate_OnANetworkFailure()
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());
        var failingHandler = StubHttpMessageHandler.Throws(
            new HttpRequestException(Generated.NewErrorMessage()));
        var neverCalledHandler = StubHttpMessageHandler.Throws(NotCalled());
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(failingHandler, neverCalledHandler),
            Keys(TwoKeys));

        // Act
        var outcome = await refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, outcome.GamesFetched);
        Assert.Empty(neverCalledHandler.Requests);
    }

    [Fact]
    public async Task RefreshCacheAsync_PersistsPartialGamesAndCursor_WhenARotatingKeyFailsMidPage()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new OpenCriticCacheRepository(dataSource);
        var handler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)),
            Json(HttpStatusCode.Unauthorized, RejectedKeyBody()));
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(handler),
            Keys(OneKey));

        // Act
        _ = await Record.ExceptionAsync(() => refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken));

        // Assert
        var savedGameCommands = dataSource.ExecutedCommands.Count(command =>
            command.ExecutedSql.Contains("INSERT INTO opencritic_cache", StringComparison.Ordinal));
        Assert.Equal(OpenCriticClient.DefaultPageSize, savedGameCommands);
        var cursorWrite = dataSource.ExecutedCommands.Last(command =>
            command.ExecutedSql.Contains("INSERT INTO opencritic_pagination_cursor", StringComparison.Ordinal));
        Assert.Equal(OpenCriticClient.DefaultPageSize, cursorWrite.Parameters[CuratorSqlParameters.NextSkip].Value);
    }

    [Fact]
    public async Task RefreshCacheAsync_PersistsPartialProgress_OnANonRotatingFailureToo()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new OpenCriticCacheRepository(dataSource);
        var handler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var neverCalledHandler = StubHttpMessageHandler.Throws(NotCalled());
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(handler, neverCalledHandler),
            Keys(TwoKeys));

        // Act
        var outcome = await refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, outcome.GamesFetched);
        Assert.Empty(neverCalledHandler.Requests);
        var savedGameCommands = dataSource.ExecutedCommands.Count(command =>
            command.ExecutedSql.Contains("INSERT INTO opencritic_cache", StringComparison.Ordinal));
        Assert.Equal(OpenCriticClient.DefaultPageSize, savedGameCommands);
    }

    [Fact]
    public async Task RefreshCacheAsync_RotatingKeyResumesFromThePriorKeysAdvancedCursor_NotTheOriginalStartSkip()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        EnqueuePlaceholders(dataSource, 1 + OpenCriticClient.DefaultPageSize + 1);
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(OpenCriticClient.DefaultPageSize));
        var repository = new OpenCriticCacheRepository(dataSource);
        var keyOneHandler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)),
            Json(HttpStatusCode.Unauthorized, RejectedKeyBody()));
        var keyTwoHandler = StubHttpMessageHandler.Returns(JsonResponse.OkEmptyArray());
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(keyOneHandler, keyTwoHandler),
            Keys(TwoKeys));

        // Act
        await refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            SkipQuery(OpenCriticClient.DefaultPageSize),
            keyTwoHandler.Requests[0].RequestUri?.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCacheAsync_WhenEveryKeyIsRejected_ThrowsEnrichmentAuthException()
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());
        var first = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var second = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(first, second),
            Keys(TwoKeys));

        // Act
        var exception = await Record.ExceptionAsync(
            () => refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<EnrichmentAuthException>(exception);
        Assert.Equal(EnrichmentProvider.OpenCritic, authException.Provider);
    }

    [Fact]
    public async Task RefreshCacheAsync_WhenEveryKeyIsRateLimited_ThrowsEnrichmentRateLimitExceptionWithTheRetryAfterHint()
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());
        var retryAfterSeconds = Generated.NewRetryAfterSeconds();
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            TimeSpan.FromSeconds(retryAfterSeconds));
        var only = StubHttpMessageHandler.Returns(response);
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(only),
            Keys(OneKey));

        // Act
        var exception = await Record.ExceptionAsync(
            () => refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken));

        // Assert
        var rateLimitException = Assert.IsType<EnrichmentRateLimitException>(exception);
        Assert.Equal(retryAfterSeconds, rateLimitException.RetryAfterSeconds);
    }

    [Fact]
    public async Task RefreshCacheAsync_HonoursTheConfiguredPageCap()
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());
        var handler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)),
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)));
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(handler),
            Keys(1),
            maxPagesPerRun: 1);

        // Act
        await refresher.RefreshCacheAsync([OpenCriticPlatforms.Ps4], TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RefreshCacheAsync_SweepsEveryPlatformAndSumsTheGameCounts()
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());
        var handler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Games(NewGameEntry())),
            Json(HttpStatusCode.OK, Games(NewGameEntry())));
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(handler),
            Keys(OneKey));

        // Act
        var outcome = await refresher.RefreshCacheAsync(OpenCriticPlatforms.All, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OneGamePerPlatform * OpenCriticPlatforms.All.Count, outcome.GamesFetched);
        Assert.Contains(
            PlatformQuery(OpenCriticPlatforms.Ps4),
            handler.Requests[0].RequestUri?.Query,
            StringComparison.Ordinal);
        Assert.Contains(
            PlatformQuery(OpenCriticPlatforms.Ps5),
            handler.Requests[1].RequestUri?.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCacheAsync_StopsAtTheFirstPlatformThatExhaustsEveryKey()
    {
        // Arrange
        var repository = new OpenCriticCacheRepository(new FakeDbDataSource());
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(handler),
            Keys(OneKey));

        // Act
        var exception = await Record.ExceptionAsync(
            () => refresher.RefreshCacheAsync(OpenCriticPlatforms.All, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<EnrichmentAuthException>(exception);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RefreshCacheAsync_SkipsAPlatformWithoutSpendingAnApiCall_WhenAnotherRunHoldsItsCursorLock()
    {
        // Arrange
        var dataSource = new FakeDbDataSource { GrantsAdvisoryLocks = false };
        var repository = new OpenCriticCacheRepository(dataSource);
        var neverCalledHandler = StubHttpMessageHandler.Throws(NotCalled());
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(neverCalledHandler),
            Keys(OneKey));

        // Act
        var outcome = await refresher.RefreshCacheAsync(OpenCriticPlatforms.All, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(neverCalledHandler.Requests);
        Assert.Equal(NoGames, outcome.GamesFetched);
        Assert.Equal(NoPlatforms, outcome.ProcessedPlatformCount);
        Assert.Equal(OpenCriticPlatforms.All, outcome.ContendedPlatforms);
        Assert.True(outcome.EveryPlatformContended);
    }

    [Fact]
    public async Task RefreshCacheAsync_ReleasesEachPlatformsCursorLock_BeforeMovingToTheNext()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new OpenCriticCacheRepository(dataSource);
        var handler = StubHttpMessageHandler.Sequence(
            [.. OpenCriticPlatforms.All.Select(_ => JsonResponse.OkEmptyArray())]);
        var refresher = new OpenCriticAdminRefreshService(
            repository,
            RoutingClient(handler),
            Keys(OneKey));

        // Act
        await refresher.RefreshCacheAsync(OpenCriticPlatforms.All, TestContext.Current.CancellationToken);

        // Assert
        var acquires = dataSource.ExecutedCommands.Count(command =>
            command.ExecutedSql.Contains(AdvisoryLockHandle.TryAcquireFunctionName, StringComparison.Ordinal));
        var releases = dataSource.ExecutedCommands.Count(command =>
            command.ExecutedSql.Contains(AdvisoryLockHandle.ReleaseFunctionName, StringComparison.Ordinal));
        Assert.Equal(OpenCriticPlatforms.All.Count, acquires);
        Assert.Equal(OpenCriticPlatforms.All.Count, releases);
    }

    private static void EnqueuePlaceholders(FakeDbDataSource dataSource, int count)
    {
        for (var i = 0; i < count; i++)
        {
            dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        }
    }

    private static OpenCriticClient RoutingClient(params StubHttpMessageHandler[] handlers) =>
        new(new HttpClient(new KeyRoutingHandler(handlers)), Generated.NewProviderBaseAddress());

    private static IReadOnlyList<OpenCriticCredential> Keys(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new OpenCriticCredential
        {
            RapidApiKey = KeyFor(index),
        })];

    private static string KeyFor(int index) =>
        $"{KeyPrefix}{index.ToString(CultureInfo.InvariantCulture)}";

    private static OpenCriticClient NewClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), Generated.NewProviderBaseAddress());

    private static InvalidOperationException NotCalled() => new("This collaborator must not be called.");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        JsonResponse.WithStatus(status, body);

    private static string PlatformQuery(string platform) =>
        $"{OpenCriticClient.PlatformsQueryKey}={platform}";

    private static string SkipQuery(int skip) =>
        $"{OpenCriticClient.SkipQueryKey}={skip.ToString(CultureInfo.InvariantCulture)}";

    private static string RejectedKeyBody() =>
        JsonSerializer.Serialize(new { message = Generated.NewErrorMessage() }, OpenCriticWireFormat);

    private static OpenCriticGameEntry NewGameEntry() =>
        new()
        {
            Id = Generated.NewOpenCriticGameId(),
            Name = Generated.NewGameTitle(),
            TopCriticScore = Generated.NewCriticScore(),
            Tier = Generated.NewOpenCriticTier(),
            PercentRecommended = Generated.NewPercentRecommended(),
        };

    private static string Page(int entries, int startId = FirstPageStartId) =>
        JsonSerializer.Serialize(
            Enumerable.Range(startId, entries).Select(index => NewGameEntry() with { Id = index }),
            OpenCriticWireFormat);

    private static string Games(params OpenCriticGameEntry[] entries) =>
        JsonSerializer.Serialize(entries, OpenCriticWireFormat);

    private sealed class KeyRoutingHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<StubHttpMessageHandler> _handlers;

        public KeyRoutingHandler(IReadOnlyList<StubHttpMessageHandler> handlers) => _handlers = handlers;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = request.Headers.GetValues(OpenCriticClient.RapidApiKeyHeader).Single();
            var index = int.Parse(key[KeyPrefix.Length..], CultureInfo.InvariantCulture);
            return new HttpMessageInvoker(_handlers[index]).SendAsync(request, cancellationToken);
        }
    }
}
