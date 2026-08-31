namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.Rawg;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class RawgClientTests
{
    private static readonly RawgCredential Credential = new() { ApiKey = Guid.NewGuid().ToString() };

    private static readonly JsonSerializerOptions RawgWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task SearchGamesAsync_SendsTheKeyAndSearchTermAsQueryParameters()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, SearchBody()));
        var client = NewClient(handler);

        // Act
        await client.SearchGamesAsync(gameTitle, Credential, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/api/{RawgClient.GamesRoute}", request.RequestUri?.AbsolutePath);
        Assert.Contains($"key={Credential.ApiKey}", request.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains($"search={gameTitle}", request.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains($"page_size={RawgClient.DefaultSearchPageSize}", request.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("search_precise=false", request.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchGamesAsync_WhenAResultCarriesNoId_SkipsItAndKeepsTheRest()
    {
        // Arrange
        var expectedGameId = NewRawgGameId();
        var usable = new RawgSearchResult { Id = expectedGameId, Name = NewGameTitle() };
        var idless = new RawgSearchResult { Name = NewGameTitle() };
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, SearchBody(idless, usable)));
        var client = NewClient(handler);

        // Act
        var results = await client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(results);
        Assert.Equal(expectedGameId, candidate.RawgGameId);
    }

    [Fact]
    public async Task SearchGamesAsync_WhenAResultCarriesNoName_SkipsItBecauseItCouldNeverMatch()
    {
        // Arrange
        var expectedGameId = NewRawgGameId();
        var usable = new RawgSearchResult { Id = expectedGameId, Name = NewGameTitle() };
        var nameless = new RawgSearchResult { Id = NewRawgGameId() };
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, SearchBody(nameless, usable)));
        var client = NewClient(handler);

        // Act
        var results = await client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(results);
        Assert.Equal(expectedGameId, candidate.RawgGameId);
    }

    [Fact]
    public async Task SearchGamesAsync_CarriesTheMetacriticScoreAndEsrbRatingTheSearchRowAlreadyProvides()
    {
        // Arrange
        var expectedMetacritic = NewMetacriticScore();
        var expectedEsrbRating = NewEsrbRatingName();
        var result = new RawgSearchResult
        {
            Id = NewRawgGameId(),
            Name = NewGameTitle(),
            Metacritic = expectedMetacritic,
            EsrbRating = new RawgNamed { Name = expectedEsrbRating },
        };
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, SearchBody(result)));
        var client = NewClient(handler);

        // Act
        var results = await client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(results);
        Assert.Equal(expectedMetacritic, candidate.Metacritic);
        Assert.Equal(expectedEsrbRating, candidate.EsrbRating?.Name);
    }

    [Fact]
    public async Task SearchGamesAsync_WhenTheSearchRowOmitsMetacriticAndEsrbRating_LeavesThemNullRatherThanDefaulting()
    {
        // Arrange
        var result = new RawgSearchResult { Id = NewRawgGameId(), Name = NewGameTitle() };
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, SearchBody(result)));
        var client = NewClient(handler);

        // Act
        var results = await client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(results);
        Assert.Null(candidate.Metacritic);
        Assert.Null(candidate.EsrbRating);
    }

    [Fact]
    public async Task SearchGamesAsync_WhenRawgSendsABodyThisClientCannotRead_RaisesRawgApiExceptionRatherThanEscaping()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, """{"results": 5}"""));
        var client = NewClient(handler);

        // Act
        var exception = await Record.ExceptionAsync(() => client.SearchGamesAsync(
            NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<RawgApiException>(exception);
    }

    [Fact]
    public async Task SearchGamesAsync_ParsesResultsIntoCandidatesWithTheirPlatformIds()
    {
        // Arrange
        var rawgGameId = NewRawgGameId();
        var gameTitle = NewGameTitle();
        var releaseDate = NewReleaseDateText();
        var firstPlatformId = NewPlatformId();
        var secondPlatformId = NewPlatformId();
        var result = new RawgSearchResult
        {
            Id = rawgGameId,
            Name = gameTitle,
            Released = releaseDate,
            Platforms = [PlatformEntry(firstPlatformId, NewPlatformName()), PlatformEntry(secondPlatformId, NewPlatformName())],
        };
        var body = SearchBody(result);
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, body));
        var client = NewClient(handler);

        // Act
        var results = await client.SearchGamesAsync(gameTitle, Credential, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(results);
        Assert.Equal(rawgGameId, candidate.RawgGameId);
        Assert.Equal(gameTitle, candidate.Name);
        Assert.Equal(releaseDate, candidate.Released);
        Assert.Contains(firstPlatformId, candidate.PlatformIds);
        Assert.Contains(secondPlatformId, candidate.PlatformIds);
    }

    [Fact]
    public async Task SearchGamesAsync_WhenTheKeyIsRejected_RaisesAnErrorThatDoesNotLeakTheBodyOrKeyIntoItsMessage()
    {
        // Arrange
        var rejectionReason = NewRejectionReason();
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Unauthorized, $"{{\"detail\":\"{rejectionReason}\"}}"));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<RawgApiException>(
            () => client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((int)HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain(rejectionReason, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential.ApiKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task FetchDetailAsync_WhenAccessIsForbidden_RaisesWithTheStatusCodePreservedAndTheBodyRedacted()
    {
        // Arrange
        var rawgGameId = NewRawgGameId();
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Forbidden, $"{{\"detail\":\"forbidden for key {Credential.ApiKey}\"}}"));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<RawgApiException>(
            () => client.FetchDetailAsync(rawgGameId, Credential, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((int)HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(Credential.ApiKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential.ApiKey, exception.ProviderDetail, StringComparison.Ordinal);
        Assert.Contains(RawgCredential.RedactedPlaceholder, exception.ProviderDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderDetail_IsTruncatedSoALongProviderBodyCannotFloodTheRunSummary()
    {
        // Arrange
        var oversizedProviderBody = new string('x', RawgClient.MaxProviderDetailChars + Random.Shared.Next(50, 900));
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.InternalServerError, oversizedProviderBody));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<RawgApiException>(
            () => client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(RawgClient.MaxProviderDetailChars + "...".Length, exception.ProviderDetail?.Length);
        Assert.EndsWith("...", exception.ProviderDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchGamesAsync_OnRateLimitWithARetryAfterHeader_ParsesRetryAfterSeconds()
    {
        // Arrange
        var retryAfterSeconds = Random.Shared.Next(1, 999);
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", retryAfterSeconds.ToString(CultureInfo.InvariantCulture));
        var handler = StubHttpMessageHandler.Returns(response);
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<RawgApiException>(
            () => client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((int)HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal((double)retryAfterSeconds, exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task SearchGamesAsync_OnRateLimitWithoutARetryAfterHeader_LeavesItNullSoTheCallerAppliesItsOwnBackoff()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<RawgApiException>(
            () => client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((int)HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Null(exception.RetryAfterSeconds);
    }

    [Fact]
    public async Task SearchGamesAsync_WhenTheTransportFails_PropagatesTheRawExceptionUnwrapped()
    {
        // Arrange
        var transportFailureMessage = NewTransportFailureMessage();
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(transportFailureMessage));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SearchGamesAsync(NewGameTitle(), Credential, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(transportFailureMessage, exception.Message);
    }

    [Fact]
    public async Task FetchDetailAsync_WhenTheTransportFails_PropagatesTheRawExceptionUnwrapped()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(NewTransportFailureMessage()));
        var client = NewClient(handler);

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.FetchDetailAsync(NewRawgGameId(), Credential, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<HttpRequestException>(exception);
    }

    [Fact]
    public async Task ValidateKeyAsync_SpendsOneRequestOnTheGenresEndpointNeverTheSearchEndpoint()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, SearchBody()));
        var client = NewClient(handler);

        // Act
        await client.ValidateKeyAsync(Credential, TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/api/{RawgClient.GenresRoute}", request.RequestUri?.AbsolutePath);
        Assert.Contains($"page_size={RawgClient.ValidateKeyPageSize}", request.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateKeyAsync_WhenTheKeyIsRejected_RaisesAnErrorThatDoesNotLeakTheBodyOrKeyIntoItsMessage()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Unauthorized, $"{{\"detail\":\"{NewRejectionReason()}\"}}"));
        var client = NewClient(handler);

        // Act
        var exception = await Assert.ThrowsAsync<RawgApiException>(
            () => client.ValidateKeyAsync(Credential, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal((int)HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain(Credential.ApiKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchDetailAsync_WhenRawgReturns404_ReturnsNullWithoutRaising()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = NewClient(handler);

        // Act
        var detail = await client.FetchDetailAsync(NewRawgGameId(), Credential, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(detail);
    }

    [Fact]
    public async Task FetchDetailAsync_OnSuccess_ParsesTheDetailAndKeepsTheBodyVerbatimInRaw()
    {
        // Arrange
        var rawgGameId = NewRawgGameId();
        var expectedMetacritic = NewMetacriticScore();
        var body = DetailBody(new RawgGameDetail { Metacritic = expectedMetacritic });
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, body));
        var client = NewClient(handler);

        // Act
        var detail = await client.FetchDetailAsync(rawgGameId, Credential, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(detail);
        Assert.Equal(expectedMetacritic, detail.Detail.Metacritic);
        Assert.Equal(body, detail.Raw);
    }

    private static string SearchBody(params RawgSearchResult[] results) =>
        JsonSerializer.Serialize(new RawgSearchResponse { Results = results }, RawgWireFormat);

    private static string DetailBody(RawgGameDetail detail) =>
        JsonSerializer.Serialize(detail, RawgWireFormat);

    private static int NewRawgGameId() => TestValues.NewRawgGameId();

    private static double NewMetacriticScore() => Random.Shared.Next(1, 101);

    private static int NewPlatformId() => Random.Shared.Next(1, 1_000);

    private static string NewGameTitle() => $"game{Guid.NewGuid():N}";

    private static string NewPlatformName() => $"platform{Guid.NewGuid():N}";

    private static string NewEsrbRatingName() => $"esrb{Guid.NewGuid():N}";

    private static string NewRejectionReason() => $"Invalid API key {Guid.NewGuid():N}";

    private static string NewTransportFailureMessage() => $"transport-failure-{Guid.NewGuid():N}";

    private static string NewReleaseDateText() =>
        DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 3650)).UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static RawgSearchPlatformEntry PlatformEntry(int id, string name) =>
        new() { Platform = new RawgSearchPlatform { Id = id, Name = name } };

    private static RawgClient NewClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new Uri("https://api.rawg.io/api/"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
