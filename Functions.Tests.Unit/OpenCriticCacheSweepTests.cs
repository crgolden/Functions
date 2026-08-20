namespace Functions.Tests.Unit;

using System.Net;
using System.Text;
using Curator.OpenCritic;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class OpenCriticCacheSweepTests
{
    [Fact]
    public async Task Run_SpendsNoQuotaAndOpensNoConnection_WhenNoKeyIsConfigured()
    {
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(
            new InvalidOperationException("The sweep must not call OpenCritic when unconfigured."));
        var sweep = NewSweep(dataSource, handler, new Dictionary<string, string?>
        {
            ["OpenCriticEndpoint"] = "https://example.invalid",
        });

        await sweep.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task Run_SpendsNoQuotaAndOpensNoConnection_WhenEveryIndexedKeyIsBlank()
    {
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(
            new InvalidOperationException("The sweep must not call OpenCritic when unconfigured."));
        var sweep = NewSweep(dataSource, handler, new Dictionary<string, string?>
        {
            ["OpenCriticEndpoint"] = "https://example.invalid",
            ["OpenCriticRapidApiKey__0"] = string.Empty,
            ["OpenCriticRapidApiKey__1"] = "   ",
        });

        await sweep.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        Assert.Empty(handler.Requests);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public void MaxPagesPerRun_DefaultsToTheAdminRefreshCap()
    {
        Assert.Equal(20, OpenCriticCacheSweep.DefaultMaxPagesPerRun);
    }

    [Fact]
    public async Task Run_RotatesAcrossEveryConfiguredIndexedKey_WhenAnEarlierKeyIsRejected()
    {
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Sequence(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, "[]"));
        var rejectedKey = NewRapidApiKey();
        var survivingKey = NewRapidApiKey();
        var sweep = NewSweep(dataSource, handler, new Dictionary<string, string?>
        {
            ["OpenCriticEndpoint"] = "https://opencritic-api.p.rapidapi.com",
            ["OpenCriticRapidApiKey__0"] = rejectedKey,
            ["OpenCriticRapidApiKey__1"] = survivingKey,
        });

        await sweep.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        Assert.Equal(rejectedKey, SentRapidApiKey(handler, 0));
        Assert.Equal(survivingKey, SentRapidApiKey(handler, 1));
    }

    [Fact]
    public async Task Run_DoesNotRetryARejectedKeyOnTheNextPlatform_SoOneRunSpendsOneWastedRequestNotOnePerPlatform()
    {
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Sequence(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            Json(HttpStatusCode.OK, "[]"),
            Json(HttpStatusCode.OK, "[]"));
        var rejectedKey = NewRapidApiKey();
        var survivingKey = NewRapidApiKey();
        var sweep = NewSweep(dataSource, handler, new Dictionary<string, string?>
        {
            ["OpenCriticEndpoint"] = "https://opencritic-api.p.rapidapi.com",
            ["OpenCriticRapidApiKey__0"] = rejectedKey,
            ["OpenCriticRapidApiKey__1"] = survivingKey,
        });

        await sweep.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        Assert.Equal(survivingKey, SentRapidApiKey(handler, 2));
        Assert.DoesNotContain(
            handler.Requests.Skip(1),
            request => string.Equals(
                request.Headers.GetValues(OpenCriticClient.RapidApiKeyHeader).Single(),
                rejectedKey,
                StringComparison.Ordinal));
    }

    private static OpenCriticCacheSweep NewSweep(
        FakeDbDataSource dataSource,
        StubHttpMessageHandler handler,
        Dictionary<string, string?> settings)
    {
        var translated = settings.ToDictionary(pair => pair.Key.Replace("__", ":", StringComparison.Ordinal), pair => pair.Value);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(translated).Build();
        return new OpenCriticCacheSweep(
            new OpenCriticCacheRepository(dataSource),
            new OpenCriticClient(new HttpClient(handler), new Uri("https://opencritic-api.p.rapidapi.com/")),
            configuration);
    }

    private static string NewRapidApiKey() => $"rapidapi-key-{Guid.NewGuid():N}";

    private static string SentRapidApiKey(StubHttpMessageHandler handler, int requestIndex) =>
        handler.Requests[requestIndex].Headers.GetValues(OpenCriticClient.RapidApiKeyHeader).Single();

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
