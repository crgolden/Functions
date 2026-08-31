namespace Functions.Tests.Unit;

using System.Net;
using System.Text;
using System.Text.Json;
using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PsnSessionTests
{
    [Fact]
    public void VerifiedUrl_RejectsNonHttpsScheme()
    {
        // Arrange
        var url = new Uri($"http://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}");

        // Act
        var exception = Record.Exception(() => PsnSession.VerifiedUrl(url));

        // Assert
        var argumentException = Assert.IsType<ArgumentException>(exception);
        Assert.Contains(PsnSession.NonPsnUrlRefusal, argumentException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedUrl_RejectsAHostNotInThePsnAllowlist()
    {
        // Arrange
        var url = new Uri($"https://evil-{Guid.NewGuid():N}.example.com/{NewUrlPath()}");

        // Act
        var exception = Record.Exception(() => PsnSession.VerifiedUrl(url));

        // Assert
        var argumentException = Assert.IsType<ArgumentException>(exception);
        Assert.Contains(PsnSession.NonPsnUrlRefusal, argumentException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedUrl_RejectsAPathTraversalSegment()
    {
        // Arrange
        var url = new Uri($"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}/../../admin/{NewUrlPath()}");

        // Act
        var exception = Record.Exception(() => PsnSession.VerifiedUrl(url));

        // Assert
        var argumentException = Assert.IsType<ArgumentException>(exception);
        Assert.Contains(
            PsnSession.TraversalSegmentRefusal,
            argumentException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedUrl_AcceptsAnAllowlistedHttpsUrlWithNoTraversal()
    {
        // Arrange
        var url = new Uri($"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}");

        // Act
        var result = PsnSession.VerifiedUrl(url);

        // Assert
        Assert.Same(url, result);
    }

    [Fact]
    public void CreateDefaultHandler_DisablesAutomaticRedirectFollowing()
    {
        // Act
        using var handler = PsnSession.CreateDefaultHandler();

        // Assert
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void ConfigureDefaults_AppliesEveryHeaderAndTimeoutPsnExpects()
    {
        // Arrange
        using var client = new HttpClient();

        // Act
        PsnSession.ConfigureDefaults(client);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(PsnSession.TimeoutSeconds), client.Timeout);
        Assert.Equal(["en-US", "en"], client.DefaultRequestHeaders.AcceptLanguage.Select(value => value.Value));
        Assert.Equal([PsnSession.CountryHeaderValue], client.DefaultRequestHeaders.GetValues(PsnSession.CountryHeaderName));
        Assert.NotEmpty(client.DefaultRequestHeaders.GetValues("User-Agent"));
    }

    [Fact]
    public void InjectedClient_StillRefusesRedirectsAndSendsTheHeaders_WhenBuiltTheWayProgramRegistersIt()
    {
        // Arrange
        using var handler = PsnSession.CreateDefaultHandler();
        using var client = new HttpClient(handler);
        PsnSession.ConfigureDefaults(client);

        // Assert
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal([PsnSession.CountryHeaderValue], client.DefaultRequestHeaders.GetValues(PsnSession.CountryHeaderName));
    }

    [Fact]
    public async Task GetAsync_WhenTheUrlHostIsNotAllowlisted_ThrowsBeforeSendingAnyRequest()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, "[]"));
        var store = SeededStore();
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

        var hostNotOnTheAllowList = $"{Guid.NewGuid():N}.example";

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                $"https://{hostNotOnTheAllowList}/{NewUrlPath()}",
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var argumentException = Assert.IsType<ArgumentException>(exception);
        Assert.Contains(PsnSession.NonPsnUrlRefusal, argumentException.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunWithReauthAsync_WhenOperationSucceeds_InvokesItExactlyOnce()
    {
        // Arrange
        var expectedResult = Random.Shared.Next(1, 10_000);
        var calls = 0;
        var session = new PsnSession(null, null, NullPsnRateLimiter.Unthrottled);

        // Act
        var result = await session.RunWithReauthAsync(
            () =>
            {
                calls++;
                return Task.FromResult(expectedResult);
            },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedResult, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunWithReauthAsync_WhenNeitherARefreshTokenNorAnNpssoIsAvailable_PropagatesImmediatelyWithoutRetry()
    {
        // Arrange
        var rejectionMessage = NewRejectionMessage();
        var calls = 0;
        var session = new PsnSession(null, null, NullPsnRateLimiter.Unthrottled);

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () =>
            {
                calls++;
                throw new PsnAuthException(rejectionMessage);
            },
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Equal(rejectionMessage, authException.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunWithReauthAsync_WhenNoRefreshTokenButAnNpssoIsPresent_RetriesExactlyOnceAndClearsTheStaleToken()
    {
        // Arrange
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled);
        var succeededResult = Random.Shared.Next(1, 10_000);
        var operation = new RejectedOnceOperation<int>(() => succeededResult);

        // Act
        var result = await session.RunWithReauthAsync(
            operation.InvokeAsync, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(succeededResult, result);
        Assert.Equal(2, operation.Calls);
        Assert.Null(session.TokenResponse);
    }

    [Fact]
    public async Task RunWithReauthAsync_ViaTheNpssoBranch_WhenTheRetryAlsoFails_PropagatesTheSecondFailureWithoutAThirdAttempt()
    {
        // Arrange
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled);
        var rejectionPrefix = NewRejectionMessage();
        var calls = 0;

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () =>
            {
                calls++;
                throw new PsnAuthException($"{rejectionPrefix} #{calls}");
            },
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Equal(2, calls);
        Assert.Equal($"{rejectionPrefix} #{calls}", authException.Message);
    }

    [Fact]
    public async Task RunWithReauthAsync_ViaTheRefreshBranch_WhenTheRetryAlsoFails_PropagatesTheSecondFailureWithoutAThirdAttempt()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(TokenResponse(TestValues.NewAccessToken()));
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        var rejectionPrefix = NewRejectionMessage();
        var calls = 0;

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () =>
            {
                calls++;
                throw new PsnAuthException($"{rejectionPrefix} #{calls}");
            },
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Equal(2, calls);
        Assert.Equal($"{rejectionPrefix} #{calls}", authException.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RunWithReauthAsync_WhenAnAuthErrorAndARefreshTokenIsAvailable_AttemptsARefreshGrantThenRetriesOnce()
    {
        // Arrange
        var refreshedAccessToken = TestValues.NewAccessToken();
        var handler = StubHttpMessageHandler.Sequence(TokenResponse(refreshedAccessToken));
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        var operation = new RejectedOnceOperation<string?>(() => session.TokenResponse?.AccessToken);

        // Act
        var result = await session.RunWithReauthAsync(
            operation.InvokeAsync, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(refreshedAccessToken, result);
        Assert.Equal(2, operation.Calls);
        var tokenRequest = Assert.Single(handler.Requests);
        Assert.Equal("/api/authz/v3/oauth/token", tokenRequest.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task RunWithReauthAsync_WhenTheRefreshItselfIsRefused_PropagatesThatFailureWithoutFallingBackToNpsso()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(NewErrorBody(), Encoding.UTF8, "application/json"),
            });
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            TestValues.NewNpsso(),
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        var calls = 0;

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () =>
            {
                calls++;
                throw new PsnAuthException(NewRejectionMessage());
            },
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains(PsnSession.TokenExchangeFailure, authException.Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        var tokenRequest = Assert.Single(handler.Requests);
        Assert.Equal("/api/authz/v3/oauth/token", tokenRequest.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Bootstrap_ExchangesTheNpssoForAnAuthorizationCodeThenATokenWithoutFollowingTheRedirect()
    {
        // Arrange
        var accessToken = TestValues.NewAccessToken();
        var handler = StubHttpMessageHandler.Sequence(Authorize302(), TokenResponse(accessToken), Json(HttpStatusCode.OK, "[]"));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        using var response = await session.GetAsync(
            $"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, handler.Requests.Count);
        var authorizeRequest = handler.Requests[0];
        Assert.Equal("/api/authz/v3/oauth/authorize", authorizeRequest.RequestUri?.AbsolutePath);
        var tokenRequest = handler.Requests[1];
        Assert.Equal("/api/authz/v3/oauth/token", tokenRequest.RequestUri?.AbsolutePath);
        var catalogRequest = handler.Requests[2];
        Assert.Equal($"{PsnSession.BearerScheme} {accessToken}", catalogRequest.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Bootstrap_WhenTheAuthorizeResponseCarriesAnErrorQueryParam_ThrowsPsnAuthException()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(RedirectTo(
            $"https://example.com/redirect?error={NewErrorCode()}"));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                $"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}",
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains("expired or is incorrect", authException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_WhenTheAuthorizeResponseCarriesNoCode_ThrowsPsnAuthExceptionNamingTheStatusCode()
    {
        // Arrange
        var statusCode = (HttpStatusCode)Random.Shared.Next(400, 500);
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(statusCode));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                $"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}",
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains($"status {(int)statusCode}", authException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_On401_ThrowsPsnAuthExceptionRatherThanAGenericHttpFailure()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            Authorize302(),
            TokenResponse(TestValues.NewAccessToken()),
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                $"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}",
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains("401", authException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_On403_ThrowsPsnAuthException()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            Authorize302(),
            TokenResponse(TestValues.NewAccessToken()),
            new HttpResponseMessage(HttpStatusCode.Forbidden));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                $"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}",
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains("403", authException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_OnAGeneric500_ThrowsAnOrdinaryHttpFailureNotPsnAuthException()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            Authorize302(),
            TokenResponse(TestValues.NewAccessToken()),
            new HttpResponseMessage((HttpStatusCode)Random.Shared.Next(500, 600)));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                $"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}",
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<HttpRequestException>(exception);
    }

    [Fact]
    public async Task GetAsync_InvokesTheInjectedRateLimiterBeforeEveryRequest()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(Authorize302(), TokenResponse(TestValues.NewAccessToken()), Json(HttpStatusCode.OK, "[]"));
        var limiter = new SpyRateLimiter();
        var session = new PsnSession(TestValues.NewNpsso(), null, limiter, new HttpClient(handler));

        // Act
        using var response = await session.GetAsync(
            $"https://{PsnSession.AllowedHosts.First()}/{NewUrlPath()}",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, limiter.AcquireCount);
    }

    [Fact]
    public async Task RestoreAsync_WithACachedToken_SkipsBootstrapAndUsesIt()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, "[]"));
        var store = SeededStore();
        var path = NewUrlPath();

        // Act
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        using var response = await session.GetAsync(
            $"https://{PsnSession.AllowedHosts.First()}/{path}",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/{path}", request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task RestoreAsync_WithNoCachedTokenAndNoNpsso_Throws()
    {
        // Arrange
        var store = new InMemoryPsnTokenStore();

        // Act
        var exception = await Record.ExceptionAsync(
            () => PsnSession.RestoreAsync(
                null, store, NullPsnRateLimiter.Unthrottled, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefreshGrant_TreatsOnlyARejectionStatusAsAnAuthFailure(HttpStatusCode status)
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(Json(status, """{"error":"invalid_grant"}"""));
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () => throw new PsnAuthException(NewRejectionMessage()),
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains("PSN token exchange failed", authException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RefreshGrant_DoesNotBurnTheCredential_WhenPsnItselfIsFailing(HttpStatusCode status)
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(Json(status, NewErrorBody()));
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () => throw new PsnAuthException(NewRejectionMessage()),
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<HttpRequestException>(exception);
        Assert.IsNotType<PsnAuthException>(exception);
    }

    [Fact]
    public async Task RefreshGrant_RaisesAnAuthFailure_WhenPsnOmitsExpiresIn()
    {
        // Arrange
        var tokenJsonWithoutExpiresIn =
            $$"""{"access_token":"{{TestValues.NewAccessToken()}}","refresh_token":"{{TestValues.NewRefreshToken()}}"}""";
        var handler = StubHttpMessageHandler.Sequence(Json(HttpStatusCode.OK, tokenJsonWithoutExpiresIn));
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () => throw new PsnAuthException(NewRejectionMessage()),
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains("missing expires_in", authException.Message, StringComparison.Ordinal);
    }

    private static InMemoryPsnTokenStore SeededStore(string? refreshToken = null)
    {
        var store = new InMemoryPsnTokenStore();
        store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = TestValues.NewAccessToken(),
                RefreshToken = refreshToken,
                ExpiresIn = NewExpiresInSeconds(),
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage Authorize302() => RedirectTo(
        $"https://example.com/redirect?code={TestValues.NewAuthorizationCode()}");

    private static HttpResponseMessage RedirectTo(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private static HttpResponseMessage TokenResponse(string accessToken) => Json(
        HttpStatusCode.OK,
        TokenEndpointJson(accessToken));

    private static string TokenEndpointJson(string accessToken) =>
        JsonSerializer.Serialize(new PsnTokenEndpointResponse
        {
            AccessToken = accessToken,
            RefreshToken = TestValues.NewRefreshToken(),
            ExpiresIn = NewExpiresInSeconds(),
            RefreshTokenExpiresIn = Random.Shared.Next(86_400, 5_184_000),
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string NewUrlPath() => $"path-{Guid.NewGuid():N}";

    private static string NewErrorCode() => TestValues.NewErrorMessage();

    private static string NewErrorBody() => $"upstream-failure-{Guid.NewGuid():N}";

    private static string NewRejectionMessage() => $"rejected-{Guid.NewGuid():N}";

    private static int NewExpiresInSeconds() => Random.Shared.Next(60, 86_400);

    private sealed class RejectedOnceOperation<T>
    {
        private readonly Func<T> _succeed;

        public RejectedOnceOperation(Func<T> succeed) => _succeed = succeed;

        public int Calls { get; private set; }

        public Task<T> InvokeAsync()
        {
            Calls++;
            if (Calls == 1)
            {
                throw new PsnAuthException(NewRejectionMessage());
            }

            return Task.FromResult(_succeed());
        }
    }

    private sealed class SpyRateLimiter : IPsnRateLimiter
    {
        public int AcquireCount { get; private set; }

        public Task AcquireAsync(CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            return Task.CompletedTask;
        }
    }
}
