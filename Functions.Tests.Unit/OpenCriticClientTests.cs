namespace Functions.Tests.Unit;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Curator.OpenCritic;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class OpenCriticClientTests
{
    private const string NearExhaustedRemainingRequests = "5";

    private const int ShortPageGameCount = 1;

    private const int SecondPageStartId = 100;

    private static readonly OpenCriticCredential Credential =
        new() { RapidApiKey = Guid.NewGuid().ToString() };

    private static readonly JsonSerializerOptions OpenCriticWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task ValidateKeyAsync_SpendsOneRequestOnTheCatalogEndpointNeverTheSearchEndpoint()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, "[]"));
        var client = NewClient(handler);

        // Act
        await client.ValidateKeyAsync(Credential, TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/game", request.RequestUri?.AbsolutePath);
        Assert.Contains("platforms=ps5", request.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Equal(Credential.RapidApiKey, request.Headers.GetValues("x-rapidapi-key").Single());
    }

    [Fact]
    public async Task ValidateKeyAsync_WhenTheKeyIsRejected_RaisesAnErrorThatDoesNotLeakTheBodyIntoItsMessage()
    {
        // Arrange
        var providerMessage = TestValues.NewErrorMessage();
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Unauthorized, ProviderMessageBody(providerMessage)));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.ValidateKeyAsync(Credential, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((int)HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain(providerMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderDetail_CarriesTheResponseBodySoAnUnsubscribedPlanIsDistinguishableFromABadKey()
    {
        // Arrange
        var unsubscribedPlanMessage = TestValues.NewErrorMessage();
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Forbidden, ProviderMessageBody(unsubscribedPlanMessage)));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.ValidateKeyAsync(Credential, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(unsubscribedPlanMessage, exception.ProviderDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderDetail_RedactsTheApiKeyWhenTheBodyEchoesItBack()
    {
        // Arrange
        var bodyEchoingTheKeyBack = ProviderMessageBody(
            $"{TestValues.NewErrorMessage()} {Credential.RapidApiKey}");
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Unauthorized, bodyEchoingTheKeyBack));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.ValidateKeyAsync(Credential, TestContext.Current.CancellationToken));

        // Assert
        Assert.DoesNotContain(Credential.RapidApiKey, exception.ProviderDetail, StringComparison.Ordinal);
        Assert.Contains(
            OpenCriticCredential.RedactedPlaceholder,
            exception.ProviderDetail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderDetail_IsTruncatedSoALongProviderBodyCannotFloodTheRunSummary()
    {
        // Arrange
        var oversizedProviderBody = new string(
            'x', OpenCriticClient.MaxProviderDetailChars + Random.Shared.Next(50, 900));
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.InternalServerError, oversizedProviderBody));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.ValidateKeyAsync(Credential, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(
            OpenCriticClient.MaxProviderDetailChars + OpenCriticClient.TruncationSuffix.Length,
            exception.ProviderDetail?.Length);
        Assert.EndsWith(OpenCriticClient.TruncationSuffix, exception.ProviderDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_StopsOnAShortPageAndResetsTheCursor()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Page(1)));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            "ps5",
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(result.Games);
        Assert.Equal(0, game.OcGameId);
        Assert.True(result.Exhausted);
        Assert.Equal(0, result.NextSkip);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_TreatsANegativeTopCriticScoreAsUnscored()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(
            Json(
                HttpStatusCode.OK,
                Games(new OpenCriticGameEntry
                {
                    Id = TestValues.NewOpenCriticGameId(),
                    Name = TestValues.NewGameTitle(),
                    TopCriticScore = -TestValues.NewCriticScore(),
                    Tier = TestValues.NewOpenCriticTier(),
                })));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            "ps5",
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.Games[0].TopCriticScore);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_PaginatesUntilAShortPageArrives()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)),
            Json(HttpStatusCode.OK, Page(ShortPageGameCount, startId: SecondPageStartId)));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            "ps4",
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OpenCriticClient.DefaultPageSize + ShortPageGameCount, result.Games.Count);
        Assert.True(result.Exhausted);
        Assert.Contains("skip=0", handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains(
            $"skip={OpenCriticClient.DefaultPageSize}",
            handler.Requests[1].RequestUri?.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_StopsWhenTheDailyRequestBudgetIsNearlyExhausted()
    {
        // Arrange
        var response = Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize));
        response.Headers.Add(OpenCriticClient.RemainingRequestsHeader, NearExhaustedRemainingRequests);
        var handler = StubHttpMessageHandler.Returns(response);
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            "ps5",
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(handler.Requests);
        Assert.False(result.Exhausted);
        Assert.Equal(OpenCriticClient.DefaultPageSize, result.NextSkip);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_AnEmptyPageEndsTheSweepImmediately()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, "[]"));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            "ps5",
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result.Games);
        Assert.True(result.Exhausted);
        Assert.Equal(0, result.NextSkip);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_ResumesFromTheStoredCursor()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, "[]"));
        var client = NewClient(handler);

        // Act
        await client.FetchPlatformGamesAsync(
            "ps5",
            Credential,
            startSkip: 3800,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("skip=3800", handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_HonoursThePageCapSoOneRunCannotBurnTheWholeDailyBudget()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)), Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            "ps5",
            Credential,
            maxPages: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(handler.Requests);
        Assert.False(result.Exhausted);
        Assert.Equal(OpenCriticClient.DefaultPageSize, result.NextSkip);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_OnANon2xx_RaisesWithoutChainingTheUnderlyingHttpError()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"invalid key\"}"));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.FetchPlatformGamesAsync("ps5", Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(401, exception.StatusCode);
        Assert.DoesNotContain("invalid key", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_ParsesRetryAfterSecondsSoTheRunCanBeRescheduled()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "60");
        var handler = StubHttpMessageHandler.Returns(response);
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.FetchPlatformGamesAsync("ps5", Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(429, exception.StatusCode);
        Assert.Equal(60.0, exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_WhenRetryAfterIsAbsent_LeavesItNullSoTheCallerAppliesItsOwnBackoff()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.FetchPlatformGamesAsync("ps5", Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_WhenAPageFails_KeepsTheEarlierPagesAndResumesFromTheFailedOffset()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)), Json(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}"));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.FetchPlatformGamesAsync("ps5", Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(OpenCriticClient.DefaultPageSize, exception.PartialGames?.Count);
        Assert.Equal(OpenCriticClient.DefaultPageSize, exception.PartialNextSkip);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_WhenTheTransportFails_WrapsItWithTheSamePartialProgress()
    {
        // Arrange
        var handler = StubHttpMessageHandler.SequenceThen(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)), new HttpRequestException("boom"));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticNetworkException>(
            () => client.FetchPlatformGamesAsync("ps5", Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(OpenCriticClient.DefaultPageSize, exception.PartialGames.Count);
        Assert.Equal(OpenCriticClient.DefaultPageSize, exception.PartialNextSkip);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_SkipsEntriesMissingAnIdOrName()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(
            HttpStatusCode.OK,
            "[{\"id\":1,\"name\":\"Kept\"},{\"name\":\"No id\"},{\"id\":3},{\"id\":4,\"name\":\"\"}]"));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            "ps5",
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(result.Games);
        Assert.Equal("Kept", game.Name);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_CarriesTheProviderPayloadForPersistence()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.OK, "[{\"id\":1,\"name\":\"Kept\",\"tier\":\"Mighty\"}]"));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            "ps5",
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("\"tier\":\"Mighty\"", result.Games[0].Raw, StringComparison.Ordinal);
    }

    private static OpenCriticClient NewClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new Uri("https://opencritic-api.p.rapidapi.com/"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ProviderMessageBody(string message) =>
        JsonSerializer.Serialize(new { message }, OpenCriticWireFormat);

    private static string Page(int entries, int startId = 0) =>
        JsonSerializer.Serialize(
            Enumerable.Range(startId, entries).Select(index => new OpenCriticGameEntry
            {
                Id = index,
                Name = $"Game {index}",
                TopCriticScore = 70,
                Tier = TestValues.NewOpenCriticTier(),
                PercentRecommended = 50,
            }),
            OpenCriticWireFormat);

    private static string Games(params OpenCriticGameEntry[] entries) =>
        JsonSerializer.Serialize(entries, OpenCriticWireFormat);
}
