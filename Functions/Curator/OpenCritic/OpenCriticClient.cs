namespace Functions.Curator.OpenCritic;

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

public sealed class OpenCriticClient : IOpenCriticClient
{
    public const int DefaultPageSize = 20;

    internal const string RapidApiKeyHeader = "x-rapidapi-key";
    internal const string RemainingRequestsHeader = "X-RateLimit-Requests-Remaining";
    internal const int MaxProviderDetailChars = 300;
    internal const string TruncationSuffix = "...";

    private const string RapidApiHost = "opencritic-api.p.rapidapi.com";
    private const int MinimumRemainingRequests = 10;

    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;

    public OpenCriticClient(HttpClient httpClient, Uri baseAddress)
    {
        _httpClient = httpClient;
        _baseAddress = baseAddress;
    }

    public async Task ValidateKeyAsync(
        OpenCriticCredential credential,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("ps5", credential, skip: 0, cancellationToken);
        await ThrowIfUnsuccessfulAsync(response, credential, cancellationToken);
    }

    public async Task<OpenCriticPaginationResult> FetchPlatformGamesAsync(
        string platform,
        OpenCriticCredential credential,
        int startSkip = 0,
        int? maxPages = null,
        CancellationToken cancellationToken = default)
    {
        var games = new List<OpenCriticGame>();
        var skip = startSkip;
        var pagesFetched = 0;
        var exhausted = false;

        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(platform, credential, skip, cancellationToken);
            }
            catch (Exception exc) when (IsTransportFailure(exc, cancellationToken))
            {
                throw new OpenCriticNetworkException(games, skip, exc);
            }

            using (response)
            {
                try
                {
                    await ThrowIfUnsuccessfulAsync(response, credential, cancellationToken);
                }
                catch (OpenCriticApiException exc)
                {
                    exc.PartialGames = games;
                    exc.PartialNextSkip = skip;
                    throw;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var entries = JsonSerializer.Deserialize<List<OpenCriticGameEntry>>(body) ?? [];
                if (entries.Count == 0)
                {
                    exhausted = true;
                    break;
                }

                var count = entries.Count;
                foreach (var entry in entries)
                {
                    var game = entry.ToGame(JsonSerializer.Serialize(entry));
                    if (game is not null)
                    {
                        games.Add(game);
                    }
                }

                skip += DefaultPageSize;

                if (RemainingRequests(response) is { } remaining && remaining < MinimumRemainingRequests)
                {
                    break;
                }

                if (count < DefaultPageSize)
                {
                    exhausted = true;
                    break;
                }
            }

            pagesFetched++;
            if (maxPages is { } cap && pagesFetched >= cap)
            {
                break;
            }
        }

        return new OpenCriticPaginationResult(games, exhausted ? 0 : skip, exhausted);
    }

    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static int? RemainingRequests(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(RemainingRequestsHeader, out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault();
        return value is not null && value.All(char.IsAsciiDigit) && value.Length > 0
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : null;
    }

    private static double? RetryAfterSeconds(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta.TotalSeconds;
        }

        return retryAfter.Date is { } date
            ? Math.Max((date - DateTimeOffset.UtcNow).TotalSeconds, 0.0)
            : null;
    }

    private static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        OpenCriticCredential credential,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new OpenCriticApiException(
            (int)response.StatusCode,
            RetryAfterSeconds(response),
            await ProviderDetailAsync(response, credential, cancellationToken));
    }

    private static async Task<string?> ProviderDetailAsync(
        HttpResponseMessage response,
        OpenCriticCredential credential,
        CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return null;
        }

        text = credential.Redact(text);
        return text.Length > MaxProviderDetailChars
            ? text[..MaxProviderDetailChars] + TruncationSuffix
            : text;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string platform,
        OpenCriticCredential credential,
        int skip,
        CancellationToken cancellationToken)
    {
        var relative = string.Create(
            CultureInfo.InvariantCulture,
            $"game?platforms={Uri.EscapeDataString(platform)}&sort=name&order=asc&skip={skip}");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress, relative));
        request.Headers.Add("x-rapidapi-host", RapidApiHost);
        request.Headers.Add(RapidApiKeyHeader, credential.RapidApiKey);
        return await _httpClient.SendAsync(request, cancellationToken);
    }
}
