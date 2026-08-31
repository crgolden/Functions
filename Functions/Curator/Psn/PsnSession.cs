namespace Functions.Curator.Psn;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class PsnSession : IAsyncDisposable
{
    public const int RateLimitMaxRequests = 300;
    public const int RateLimitWindowSeconds = 15 * 60;
    public const string HttpClientName = "psn";
    internal const string BearerScheme = "Bearer";
    internal const string NonPsnUrlRefusal = "Refusing a PSN request to a non-PSN URL";
    internal const string TraversalSegmentRefusal =
        "Refusing a PSN request whose path contains a traversal segment.";

    internal const string TokenExchangeFailure = "PSN token exchange failed";
    internal const string CountryHeaderName = "Country";
    internal const string CountryHeaderValue = "US";
    internal const int TimeoutSeconds = 15;

    internal static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "ca.account.sony.com",
        "m.np.playstation.com",
        "web.np.playstation.com",
        "accounts.api.playstation.com",
        "dms.api.playstation.com",
        "us-prof.np.community.playstation.net",
    };

#pragma warning disable S1075 // fixed PSN endpoint, not environment-configurable
    private const string AuthBase = "https://ca.account.sony.com/api/authz/v3/oauth";
#pragma warning restore S1075

    private const string ClientId = "09515159-7237-4370-9b40-3806e67c0891";
    private const string Scope = "psn:mobile.v2.core psn:clientapp";
#pragma warning disable S1075 // fixed PSN OAuth redirect URI, not environment-configurable
    private const string RedirectUri = "com.scee.psxandroid.scecompcall://redirect";
#pragma warning restore S1075
    private const string BasicAuth = "Basic MDk1MTUxNTktNzIzNy00MzcwLTliNDAtMzgwNmU2N2MwODkxOnVjUGprYTV0bnRCMktxc1A=";
    private const string TokenUserAgent = "com.sony.snei.np.android.sso.share.oauth.versa.USER_AGENT";

    private const string DefaultUserAgent =
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Mobile Safari/537.36";

    private static readonly int[] TokenRejectionStatusCodes = [400, 401, 403];

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly string? _npsso;
    private readonly IPsnTokenStore? _tokenStore;
    private readonly IPsnRateLimiter _rateLimiter;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cid;

    public PsnSession(
        string? npsso,
        IPsnTokenStore? tokenStore,
        IPsnRateLimiter rateLimiter,
        HttpClient? httpClient = null)
    {
        _npsso = npsso;
        _tokenStore = tokenStore;
        _rateLimiter = rateLimiter;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _cid = Guid.NewGuid().ToString();
    }

    public PsnTokenResponse? TokenResponse { get; private set; }

    public static HttpClientHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    public static void ConfigureDefaults(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation(CountryHeaderName, CountryHeaderValue);
    }

    public static async Task<PsnSession> RestoreAsync(
        string? npsso,
        IPsnTokenStore tokenStore,
        IPsnRateLimiter rateLimiter,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var session = new PsnSession(npsso, tokenStore, rateLimiter, httpClient);
        var saved = await tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            session.TokenResponse = saved;
        }
        else if (string.IsNullOrWhiteSpace(npsso))
        {
            throw new InvalidOperationException(
                "No cached token and no npsso provided. Supply an npsso to authenticate for the first time.");
        }

        return session;
    }

    public async Task<T> RunWithReauthAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (PsnAuthException)
        {
            if (TokenResponse?.RefreshToken is { Length: > 0 })
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                return await operation().ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(_npsso))
            {
                TokenResponse = null;
                return await operation().ConfigureAwait(false);
            }

            throw;
        }
    }

    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken = default) =>
        RequestAsync(HttpMethod.Get, url, EmptyHeaders, EmptyHeaders, cancellationToken);

    public Task<HttpResponseMessage> GetAsync(
        string url,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken = default) =>
        RequestAsync(HttpMethod.Get, url, query, EmptyHeaders, cancellationToken);

    public Task<HttpResponseMessage> GetAsync(
        string url,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default) =>
        RequestAsync(HttpMethod.Get, url, query, headers, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal static Uri VerifiedUrl(Uri url)
    {
        if (!url.IsAbsoluteUri
            || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !AllowedHosts.Contains(url.Host))
        {
            throw new ArgumentException(
                $"{NonPsnUrlRefusal}: {url.Scheme}://{url.Host}",
                nameof(url));
        }

        if (RawPath(url).Split('/').Contains(".."))
        {
            throw new ArgumentException(TraversalSegmentRefusal, nameof(url));
        }

        return url;
    }

    private static string RawPath(Uri url)
    {
        var original = url.OriginalString;
        var authorityIndex = original.IndexOf(url.Authority, StringComparison.OrdinalIgnoreCase);
        if (authorityIndex < 0)
        {
            return url.AbsolutePath;
        }

        var pathStart = authorityIndex + url.Authority.Length;
        var afterAuthority = original[pathStart..];
        var queryIndex = afterAuthority.IndexOfAny(['?', '#']);
        return queryIndex >= 0 ? afterAuthority[..queryIndex] : afterAuthority;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient(CreateDefaultHandler());
        ConfigureDefaults(client);
        return client;
    }

    private static Uri BuildUrl(string url, IReadOnlyDictionary<string, string> query)
    {
        if (query is null || query.Count == 0)
        {
            return new Uri(url, UriKind.Absolute);
        }

        var builder = new StringBuilder(url);
        builder.Append(url.Contains('?', StringComparison.Ordinal) ? '&' : '?');
        var first = true;
        foreach (var (key, value) in query)
        {
            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private static string? RawHeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static Dictionary<string, string> ParseQueryString(string url)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0 || queryIndex == url.Length - 1)
        {
            return result;
        }

        var queryStart = queryIndex + 1;
        foreach (var pair in url[queryStart..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (TokenResponse is null)
        {
            await BootstrapAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (TokenResponse.AccessTokenExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_npsso))
        {
            throw new PsnAuthException("No npsso cookie available to bootstrap a new session.");
        }

        var code = await AuthorizationCodeAsync(cancellationToken).ConfigureAwait(false);
        await ExchangeAsync("authorization_code", code, refreshToken: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> AuthorizationCodeAsync(CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["access_type"] = "offline",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["response_type"] = "code",
            ["cid"] = _cid,
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl($"{AuthBase}/authorize", query));
        request.Headers.TryAddWithoutValidation("Cookie", $"npsso={_npsso}");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "com.scee.psxandroid");

        using var response = await ThrottledSendAsync(request, cancellationToken).ConfigureAwait(false);
        if (RawHeaderValue(response, "Location") is not { } location)
        {
            throw new PsnAuthException(
                $"PSN authorization did not redirect (status {(int)response.StatusCode}).");
        }

        var query2 = ParseQueryString(location);

        if (query2.ContainsKey("error"))
        {
            throw new PsnAuthException("Your npsso code has expired or is incorrect. Please generate a new one.");
        }

        if (!query2.TryGetValue("code", out var code))
        {
            throw new PsnAuthException($"PSN authorization did not return a code (status {(int)response.StatusCode}).");
        }

        return code;
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (TokenResponse?.RefreshToken is not { Length: > 0 } refreshToken)
        {
            throw new PsnAuthException("No refresh token available.");
        }

        await ExchangeAsync("refresh_token", code: null, refreshToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExchangeAsync(string grantType, string? code, string? refreshToken, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["scope"] = Scope,
            ["token_format"] = "jwt",
        };
        if (code is not null)
        {
            data["code"] = code;
            data["redirect_uri"] = RedirectUri;
            data["cid"] = _cid;
        }

        if (refreshToken is not null)
        {
            data["refresh_token"] = refreshToken;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBase}/token")
        {
            Content = new FormUrlEncodedContent(data),
        };
        request.Headers.TryAddWithoutValidation("Authorization", BasicAuth);
        request.Headers.TryAddWithoutValidation("User-Agent", TokenUserAgent);

        using var response = await ThrottledSendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        if (TokenRejectionStatusCodes.Contains(statusCode))
        {
            var snippet = body.Length > 200 ? body[..200] : body;
            throw new PsnAuthException($"{TokenExchangeFailure} ({statusCode}): {snippet}");
        }

        response.EnsureSuccessStatusCode();

        PsnTokenEndpointResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PsnTokenEndpointResponse>(body);
        }
        catch (JsonException exception)
        {
            throw new PsnAuthException("PSN token exchange returned a body that is not JSON.", exception);
        }

        if (parsed?.AccessToken is not { Length: > 0 } accessToken)
        {
            throw new PsnAuthException("PSN token exchange response is missing access_token.");
        }

        if (parsed.ExpiresIn is not { } expiresIn)
        {
            throw new PsnAuthException("PSN token exchange response is missing expires_in.");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var refreshTokenExpiresIn = parsed.RefreshTokenExpiresIn;
        var token = new PsnTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = parsed.RefreshToken,
            ExpiresIn = expiresIn,
            RefreshTokenExpiresIn = refreshTokenExpiresIn,
            AccessTokenExpiresAt = expiresIn + now,
            RefreshTokenExpiresAt = refreshTokenExpiresIn is { } refreshExpires ? refreshExpires + now : null,
        };

        TokenResponse = token;
        if (_tokenStore is not null)
        {
            await _tokenStore.SaveAsync(token, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> RequestAsync(
        HttpMethod method,
        string url,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        await EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
        var tokenResponse = TokenResponse
            ?? throw new InvalidOperationException("EnsureFreshAsync did not populate a token.");
        var accessToken = tokenResponse.AccessToken
            ?? throw new InvalidOperationException("EnsureFreshAsync populated a token with no access_token.");

        using var request = new HttpRequestMessage(method, BuildUrl(url, query));
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);

        var response = await ThrottledSendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            throw new PsnAuthException($"PSN request unauthorized/forbidden ({statusCode}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                response.EnsureSuccessStatusCode();
            }
            finally
            {
                response.Dispose();
            }
        }

        return response;
    }

    private async Task<HttpResponseMessage> ThrottledSendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = request.RequestUri is null
            ? throw new InvalidOperationException("Request has no URI.")
            : VerifiedUrl(request.RequestUri);
        await _rateLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
