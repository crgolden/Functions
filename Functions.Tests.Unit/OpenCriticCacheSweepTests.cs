namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using Curator;
using Curator.OpenCritic;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using TestSupport;
using static TestSupport.TestValues;

[Trait("Category", "Unit")]
public sealed class OpenCriticCacheSweepTests
{
    [Fact]
    public async Task Run_SpendsNoQuotaAndOpensNoConnection_WhenNoKeyIsConfigured()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(
            new InvalidOperationException("The sweep must not call OpenCritic when unconfigured."));
        var sweep = NewSweep(dataSource, handler);

        // Act
        await sweep.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(handler.Requests);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task Run_SpendsNoQuotaAndOpensNoConnection_WhenEveryIndexedKeyIsBlank()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(
            new InvalidOperationException("The sweep must not call OpenCritic when unconfigured."));
        var sweep = NewSweep(dataSource, handler, TestValues.NewBlankRun(), TestValues.NewBlankRun());

        // Act
        await sweep.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(handler.Requests);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public void MaxPagesPerRun_DefaultsToTheAdminRefreshCap()
    {
        // Act
        var defaultMaxPages = OpenCriticCacheSweep.DefaultMaxPagesPerRun;

        // Assert
        Assert.Equal(OpenCriticAdminRefreshService.AdminRefreshMaxPages, defaultMaxPages);
    }

    [Fact]
    public async Task Run_RotatesAcrossEveryConfiguredIndexedKey_WhenAnEarlierKeyIsRejected()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Sequence(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            Json(HttpStatusCode.OK, JsonResponse.EmptyArray),
            Json(HttpStatusCode.OK, JsonResponse.EmptyArray));
        var rejectedKey = NewRapidApiKey();
        var survivingKey = NewRapidApiKey();
        var sweep = NewSweep(dataSource, handler, rejectedKey, survivingKey);

        // Act
        await sweep.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rejectedKey, SentRapidApiKey(handler, 0));
        Assert.Equal(survivingKey, SentRapidApiKey(handler, 1));
    }

    [Fact]
    public async Task Run_DoesNotRetryARejectedKeyOnTheNextPlatform_SoOneRunSpendsOneWastedRequestNotOnePerPlatform()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Sequence(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            Json(HttpStatusCode.OK, JsonResponse.EmptyArray),
            Json(HttpStatusCode.OK, JsonResponse.EmptyArray));
        var rejectedKey = NewRapidApiKey();
        var survivingKey = NewRapidApiKey();
        var sweep = NewSweep(dataSource, handler, rejectedKey, survivingKey);

        // Act
        await sweep.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            [rejectedKey, survivingKey, survivingKey],
            handler.Requests.Select(request => request.Headers.GetValues(OpenCriticClient.RapidApiKeyHeader).Single()));
    }

    private static OpenCriticCacheSweep NewSweep(
        FakeDbDataSource dataSource,
        StubHttpMessageHandler handler,
        params string[] rapidApiKeys)
    {
        var indexedKeys = rapidApiKeys.Select((rapidApiKey, index) => new KeyValuePair<string, string?>(
            ConfigurationPath.Combine(CuratorConfigurationKeys.OpenCriticRapidApiKey, index.ToString(CultureInfo.InvariantCulture)),
            rapidApiKey));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(indexedKeys).Build();
        return new OpenCriticCacheSweep(
            new OpenCriticCacheRepository(dataSource),
            new OpenCriticClient(new HttpClient(handler), NewProviderBaseAddress()),
            configuration);
    }

    private static string SentRapidApiKey(StubHttpMessageHandler handler, int requestIndex) =>
        handler.Requests[requestIndex].Headers.GetValues(OpenCriticClient.RapidApiKeyHeader).Single();

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        JsonResponse.WithStatus(status, body);
}
