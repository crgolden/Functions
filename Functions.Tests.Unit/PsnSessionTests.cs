namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.Psn;
using TestSupport;
using static PsnSessionFixtureConstants;

[Trait("Category", "Unit")]
public sealed class PsnSessionTests
{
    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public void VerifiedUrl_RejectsNonHttpsScheme()
    {
        // Arrange
        var url = TestValues.NewInsecurePsnUri();

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
        var url = TestValues.NewUriOnHost(TestValues.NewHostLabel());

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
        var url = TestValues.NewPsnUriWithTraversal();

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
        var url = TestValues.NewPsnUri();

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
        Assert.Equal(
            [PsnSession.PrimaryLanguage, PsnSession.FallbackLanguage],
            client.DefaultRequestHeaders.AcceptLanguage.Select(value => value.Value));
        Assert.Equal([PsnSession.CountryHeaderValue], client.DefaultRequestHeaders.GetValues(PsnSession.CountryHeaderName));
        Assert.NotEmpty(client.DefaultRequestHeaders.GetValues(PsnSession.UserAgentHeaderName));
    }

    [Fact]
    public void InjectedClient_StillRefusesRedirectsAndSendsTheHeaders_WhenBuiltTheWayProgramRegistersIt()
    {
        // Arrange
        using var handler = PsnSession.CreateDefaultHandler();
        using var client = new HttpClient(handler);

        // Act
        PsnSession.ConfigureDefaults(client);

        // Assert
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal([PsnSession.CountryHeaderValue], client.DefaultRequestHeaders.GetValues(PsnSession.CountryHeaderName));
    }

    [Fact]
    public async Task GetAsync_WhenTheUrlHostIsNotAllowlisted_ThrowsBeforeSendingAnyRequest()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.OkEmptyArray());
        var store = SeededStore();
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

        var urlOnAHostNotInTheAllowList = TestValues.NewUriOnHost(TestValues.NewHostLabel());

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                urlOnAHostNotInTheAllowList.OriginalString,
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
        Assert.Equal(OneAttempt, calls);
    }

    [Fact]
    public async Task RunWithReauthAsync_WhenNeitherARefreshTokenNorAnNpssoIsAvailable_PropagatesImmediatelyWithoutRetry()
    {
        // Arrange
        var rejectionMessage = TestValues.NewRejectionMessage();
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
        Assert.Equal(OneAttempt, calls);
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
        Assert.Equal(OneAttemptThenOneRetry, operation.Calls);
        Assert.Null(session.TokenResponse);
    }

    [Fact]
    public async Task RunWithReauthAsync_ViaTheNpssoBranch_WhenTheRetryAlsoFails_PropagatesTheSecondFailureWithoutAThirdAttempt()
    {
        // Arrange
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled);
        var rejectionPrefix = TestValues.NewRejectionMessage();
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
        Assert.Equal(OneAttemptThenOneRetry, calls);
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
        var rejectionPrefix = TestValues.NewRejectionMessage();
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
        Assert.Equal(OneAttemptThenOneRetry, calls);
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
        Assert.Equal(OneAttemptThenOneRetry, operation.Calls);
        var tokenRequest = Assert.Single(handler.Requests);
        Assert.Equal(PsnSession.TokenPath, tokenRequest.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task RunWithReauthAsync_WhenTheRefreshItselfIsRefused_PropagatesThatFailureWithoutFallingBackToNpsso()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonResponse.Content(TestValues.NewUpstreamErrorBody()),
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
                throw new PsnAuthException(TestValues.NewRejectionMessage());
            },
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains(PsnSession.TokenExchangeFailure, authException.Message, StringComparison.Ordinal);
        Assert.Equal(OneAttempt, calls);
        var tokenRequest = Assert.Single(handler.Requests);
        Assert.Equal(PsnSession.TokenPath, tokenRequest.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Bootstrap_ExchangesTheNpssoForAnAuthorizationCodeThenATokenWithoutFollowingTheRedirect()
    {
        // Arrange
        var accessToken = TestValues.NewAccessToken();
        var handler = StubHttpMessageHandler.Sequence(Authorize302(), TokenResponse(accessToken), JsonResponse.OkEmptyArray());
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        using var response = await session.GetAsync(
            TestValues.NewPsnUri().OriginalString,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AuthorizeThenTokenThenResource, handler.Requests.Count);
        var authorizeRequest = handler.Requests[0];
        Assert.Equal(PsnSession.AuthorizePath, authorizeRequest.RequestUri?.AbsolutePath);
        var tokenRequest = handler.Requests[1];
        Assert.Equal(PsnSession.TokenPath, tokenRequest.RequestUri?.AbsolutePath);
        var catalogRequest = handler.Requests[^1];
        Assert.Equal(
            new AuthenticationHeaderValue(PsnSession.BearerScheme, accessToken).ToString(),
            catalogRequest.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Bootstrap_WhenTheAuthorizeResponseCarriesAnErrorQueryParam_ThrowsPsnAuthException()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(RedirectTo(RedirectBackWith(
            PsnSession.AuthorizationErrorQueryKey, TestValues.NewErrorMessage())));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                TestValues.NewPsnUri().OriginalString,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Equal(PsnSession.NpssoExpiredRefusal, authException.Message);
    }

    [Fact]
    public async Task Bootstrap_WhenTheAuthorizeResponseDoesNotRedirect_ThrowsPsnAuthExceptionNamingTheStatusCode()
    {
        // Arrange
        var statusCode = (HttpStatusCode)Random.Shared.Next(400, 500);
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(statusCode));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                TestValues.NewPsnUri().OriginalString,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains(
            PsnSession.AuthorizationNoRedirectRefusal, authException.Message, StringComparison.Ordinal);
        Assert.Contains(StatusNumber(statusCode), authException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_WhenTheRedirectCarriesNeitherACodeNorAnError_ThrowsPsnAuthExceptionNamingTheStatusCode()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(RedirectTo(RedirectBackWith(
            TestValues.NewJsonPropertyName(), TestValues.NewErrorMessage())));
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled, new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => session.GetAsync(
                TestValues.NewPsnUri().OriginalString,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains(
            PsnSession.AuthorizationCodeMissingRefusal, authException.Message, StringComparison.Ordinal);
        Assert.Contains(
            StatusNumber(HttpStatusCode.Found), authException.Message, StringComparison.Ordinal);
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
                TestValues.NewPsnUri().OriginalString,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains(
            PsnSession.UnauthorizedOrForbiddenRefusal, authException.Message, StringComparison.Ordinal);
        Assert.Contains(
            StatusNumber(HttpStatusCode.Unauthorized), authException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialKind_IsAUserLink_WhenTheSessionWasRestoredFromATokenStore()
    {
        // Arrange
        var session = await PsnSession.RestoreAsync(
            null,
            SeededStore(),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(StubHttpMessageHandler.Returns(JsonResponse.OkEmptyArray())),
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var credentialKind = session.CredentialKind;

        // Assert
        Assert.Equal(PsnCredentialKind.UserLink, credentialKind);
    }

    [Fact]
    public void CredentialKind_IsTheAppsOwnNpsso_WhenTheSessionHasNoTokenStore()
    {
        // Arrange
        var session = new PsnSession(TestValues.NewNpsso(), null, NullPsnRateLimiter.Unthrottled);

        // Act
        var credentialKind = session.CredentialKind;

        // Assert
        Assert.Equal(PsnCredentialKind.AppNpsso, credentialKind);
    }

    [Fact]
    public async Task GetAsync_On401_StampsTheRejectionWithTheSessionsCredentialKind()
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
                TestValues.NewPsnUri().OriginalString,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Equal(PsnCredentialKind.AppNpsso, authException.CredentialKind);
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
                TestValues.NewPsnUri().OriginalString,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains(
            PsnSession.UnauthorizedOrForbiddenRefusal, authException.Message, StringComparison.Ordinal);
        Assert.Contains(
            StatusNumber(HttpStatusCode.Forbidden), authException.Message, StringComparison.Ordinal);
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
                TestValues.NewPsnUri().OriginalString,
                cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<HttpRequestException>(exception);
    }

    [Fact]
    public async Task GetAsync_InvokesTheInjectedRateLimiterBeforeEveryRequest()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(Authorize302(), TokenResponse(TestValues.NewAccessToken()), JsonResponse.OkEmptyArray());
        var limiter = new SpyRateLimiter();
        var session = new PsnSession(TestValues.NewNpsso(), null, limiter, new HttpClient(handler));

        // Act
        using var response = await session.GetAsync(
            TestValues.NewPsnUri().OriginalString,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AuthorizeThenTokenThenResource, limiter.AcquireCount);
    }

    [Fact]
    public async Task RestoreAsync_WithACachedToken_SkipsBootstrapAndUsesIt()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.OkEmptyArray());
        var store = SeededStore();
        var url = TestValues.NewPsnUri();

        // Act
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        using var response = await session.GetAsync(
            url.OriginalString, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal(url.AbsolutePath, request.RequestUri?.AbsolutePath);
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
        var handler = StubHttpMessageHandler.Sequence(
            JsonResponse.WithStatus(status, OAuthErrorJson(OAuth2InvalidGrantError)));
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () => throw new PsnAuthException(TestValues.NewRejectionMessage()),
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains(PsnSession.TokenExchangeFailure, authException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RefreshGrant_DoesNotBurnTheCredential_WhenPsnItselfIsFailing(HttpStatusCode status)
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            JsonResponse.WithStatus(status, TestValues.NewUpstreamErrorBody()));
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () => throw new PsnAuthException(TestValues.NewRejectionMessage()),
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<HttpRequestException>(exception);
        Assert.IsNotType<PsnAuthException>(exception);
    }

    [Fact]
    public async Task RefreshGrant_RaisesAnAuthFailure_WhenPsnOmitsExpiresIn()
    {
        // Arrange
        var tokenJsonWithoutExpiresIn = JsonSerializer.Serialize(
            new PsnTokenEndpointResponse
            {
                AccessToken = TestValues.NewAccessToken(),
                RefreshToken = TestValues.NewRefreshToken(),
            },
            OmitNulls);
        var handler = StubHttpMessageHandler.Sequence(JsonResponse.Ok(tokenJsonWithoutExpiresIn));
        var store = SeededStore(refreshToken: TestValues.NewRefreshToken());
        var session = await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => session.RunWithReauthAsync<int>(
            () => throw new PsnAuthException(TestValues.NewRejectionMessage()),
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Equal(PsnSession.MissingExpiresInRefusal, authException.Message);
    }

    private static InMemoryPsnTokenStore SeededStore(string? refreshToken = null)
    {
        var store = new InMemoryPsnTokenStore();
        store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = TestValues.NewAccessToken(),
                RefreshToken = refreshToken,
                ExpiresIn = TestValues.NewExpiresInSeconds(),
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage Authorize302() => RedirectTo(RedirectBackWith(
        PsnSession.AuthorizationCodeQueryKey, TestValues.NewAuthorizationCode()));

    private static string RedirectBackWith(string queryKey, string value) =>
        $"{PsnSession.RedirectUri}?{queryKey}={value}";

    private static HttpResponseMessage RedirectTo(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.TryAddWithoutValidation(PsnSession.LocationHeaderName, location);
        return response;
    }

    private static HttpResponseMessage TokenResponse(string accessToken) =>
        JsonResponse.Ok(TokenEndpointJson(accessToken));

    private static string TokenEndpointJson(string accessToken) =>
        JsonSerializer.Serialize(new PsnTokenEndpointResponse
        {
            AccessToken = accessToken,
            RefreshToken = TestValues.NewRefreshToken(),
            ExpiresIn = TestValues.NewExpiresInSeconds(),
            RefreshTokenExpiresIn = TestValues.NewRefreshTokenExpiresInSeconds(),
        });

    private static string OAuthErrorJson(string error) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PsnSession.AuthorizationErrorQueryKey] = error,
        });

    private static string StatusNumber(HttpStatusCode status) =>
        ((int)status).ToString(CultureInfo.InvariantCulture);

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
                throw new PsnAuthException(TestValues.NewRejectionMessage());
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
