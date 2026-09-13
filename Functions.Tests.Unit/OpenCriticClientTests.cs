namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.OpenCritic;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class OpenCriticClientTests
{
    private const int ShortPageGameCount = 1;

    private const int OnePage = 1;

    private const int FirstPageSkip = 0;

    private const int CursorResetToTheStart = 0;

    private static readonly string NearExhaustedRemainingRequests =
        (OpenCriticClient.MinimumRemainingRequests - 1).ToString(CultureInfo.InvariantCulture);

    private static readonly int SecondPageStartId =
        OpenCriticClient.DefaultPageSize + TestValues.NewOpenCriticGameId();

    private static readonly OpenCriticCredential Credential =
        new() { RapidApiKey = TestValues.NewRapidApiKey() };

    private static readonly JsonSerializerOptions OpenCriticWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task ValidateKeyAsync_SpendsOneRequestOnTheCatalogEndpointNeverTheSearchEndpoint()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.OkEmptyArray());
        var client = NewClient(handler);

        // Act
        await client.ValidateKeyAsync(Credential, TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith(
            OpenCriticClient.GamePath, request.RequestUri?.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains(
            QueryPair(OpenCriticClient.PlatformsQueryKey, OpenCriticPlatforms.Ps5),
            request.RequestUri?.Query,
            StringComparison.Ordinal);
        Assert.Equal(
            Credential.RapidApiKey,
            request.Headers.GetValues(OpenCriticClient.RapidApiKeyHeader).Single());
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
            OpenCriticPlatforms.Ps5,
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(result.Games);
        Assert.Equal(0, game.OcGameId);
        Assert.True(result.Exhausted);
        Assert.Equal(CursorResetToTheStart, result.NextSkip);
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
            OpenCriticPlatforms.Ps5,
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
            OpenCriticPlatforms.Ps4,
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OpenCriticClient.DefaultPageSize + ShortPageGameCount, result.Games.Count);
        Assert.True(result.Exhausted);
        Assert.Contains(
            SkipQuery(FirstPageSkip), handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains(
            SkipQuery(OpenCriticClient.DefaultPageSize),
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
            OpenCriticPlatforms.Ps5,
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
        var handler = StubHttpMessageHandler.Returns(JsonResponse.OkEmptyArray());
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            OpenCriticPlatforms.Ps5,
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result.Games);
        Assert.True(result.Exhausted);
        Assert.Equal(CursorResetToTheStart, result.NextSkip);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_ResumesFromTheStoredCursor()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.OkEmptyArray());
        var client = NewClient(handler);

        var storedCursor = TestValues.NewPaginationCursor();

        // Act
        await client.FetchPlatformGamesAsync(
            OpenCriticPlatforms.Ps5,
            Credential,
            startSkip: storedCursor,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            SkipQuery(storedCursor), handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
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
            OpenCriticPlatforms.Ps5,
            Credential,
            maxPages: OnePage,
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
        var providerMessage = TestValues.NewErrorMessage();
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Unauthorized, ProviderMessageBody(providerMessage)));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.FetchPlatformGamesAsync(
                OpenCriticPlatforms.Ps5,
                Credential,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((int)HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain(providerMessage, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_ParsesRetryAfterSecondsSoTheRunCanBeRescheduled()
    {
        // Arrange
        var retryAfterSeconds = TestValues.NewRetryAfterSeconds();
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            TimeSpan.FromSeconds(retryAfterSeconds));
        var handler = StubHttpMessageHandler.Returns(response);
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.FetchPlatformGamesAsync(
                OpenCriticPlatforms.Ps5,
                Credential,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((int)HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(retryAfterSeconds).TotalSeconds, exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_WhenRetryAfterIsAbsent_LeavesItNullSoTheCallerAppliesItsOwnBackoff()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.FetchPlatformGamesAsync(
                OpenCriticPlatforms.Ps5,
                Credential,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_WhenAPageFails_KeepsTheEarlierPagesAndResumesFromTheFailedOffset()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            Json(HttpStatusCode.OK, Page(OpenCriticClient.DefaultPageSize)),
            Json(HttpStatusCode.Unauthorized, ProviderMessageBody(TestValues.NewErrorMessage())));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<OpenCriticApiException>(
            () => client.FetchPlatformGamesAsync(
                OpenCriticPlatforms.Ps5,
                Credential,
                cancellationToken: TestContext.Current.CancellationToken));

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
            () => client.FetchPlatformGamesAsync(
                OpenCriticPlatforms.Ps5,
                Credential,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(OpenCriticClient.DefaultPageSize, exception.PartialGames.Count);
        Assert.Equal(OpenCriticClient.DefaultPageSize, exception.PartialNextSkip);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_SkipsEntriesMissingAnIdOrName()
    {
        // Arrange
        var keptName = TestValues.NewGameTitle();
        var handler = StubHttpMessageHandler.Returns(Json(
            HttpStatusCode.OK,
            Games(
                new OpenCriticGameEntry { Id = TestValues.NewOpenCriticGameId(), Name = keptName },
                new OpenCriticGameEntry { Name = TestValues.NewGameTitle() },
                new OpenCriticGameEntry { Id = TestValues.NewOpenCriticGameId() },
                new OpenCriticGameEntry
                {
                    Id = TestValues.NewOpenCriticGameId(),
                    Name = TestValues.NewBlankRun(),
                })));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            OpenCriticPlatforms.Ps5,
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(result.Games);
        Assert.Equal(keptName, game.Name);
    }

    [Fact]
    public async Task FetchPlatformGamesAsync_CarriesTheProviderPayloadForPersistence()
    {
        // Arrange
        var tier = TestValues.NewOpenCriticTier();
        var entry = new OpenCriticGameEntry
        {
            Id = TestValues.NewOpenCriticGameId(),
            Name = TestValues.NewGameTitle(),
            Tier = tier,
        };
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Games(entry)));
        var client = NewClient(handler);

        // Act
        var result = await client.FetchPlatformGamesAsync(
            OpenCriticPlatforms.Ps5,
            Credential,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(tier, result.Games[0].Raw, StringComparison.Ordinal);
    }

    private static OpenCriticClient NewClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), TestValues.NewProviderBaseAddress());

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        JsonResponse.WithStatus(status, body);

    private static string QueryPair(string key, string value) => $"{key}={value}";

    private static string SkipQuery(int skip) =>
        QueryPair(OpenCriticClient.SkipQueryKey, skip.ToString(CultureInfo.InvariantCulture));

    private static string ProviderMessageBody(string message) =>
        JsonSerializer.Serialize(new { message }, OpenCriticWireFormat);

    private static string Page(int entries, int startId = FirstPageSkip) =>
        JsonSerializer.Serialize(
            Enumerable.Range(startId, entries).Select(index => new OpenCriticGameEntry
            {
                Id = index,
                Name = TestValues.NewGameTitle(),
                TopCriticScore = TestValues.NewCriticScore(),
                Tier = TestValues.NewOpenCriticTier(),
                PercentRecommended = TestValues.NewPercentRecommended(),
            }),
            OpenCriticWireFormat);

    private static string Games(params OpenCriticGameEntry[] entries) =>
        JsonSerializer.Serialize(entries, OpenCriticWireFormat);
}
