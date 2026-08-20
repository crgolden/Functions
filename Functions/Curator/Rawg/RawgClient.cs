namespace Functions.Curator.Rawg;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

public sealed class RawgClient : IRawgClient
{
    internal const int MaxProviderDetailChars = 300;
    internal const int DefaultSearchPageSize = 5;
    internal const int ValidateKeyPageSize = 1;
    internal const string GamesRoute = "games";
    internal const string GenresRoute = "genres";

    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;

    public RawgClient(HttpClient httpClient, Uri baseAddress)
    {
        _httpClient = httpClient;
        _baseAddress = baseAddress;
    }

    public async Task<IReadOnlyList<RawgCandidate>> SearchGamesAsync(
        string title,
        RawgCredential credential,
        int pageSize = DefaultSearchPageSize,
        CancellationToken cancellationToken = default)
    {
        (string Key, string Value)[] query =
        [
            ("key", credential.ApiKey),
            ("search", title),
            ("page_size", pageSize.ToString(CultureInfo.InvariantCulture)),
            ("search_precise", "false"),
        ];
        using var response = await SendAsync(GamesRoute, query, cancellationToken);
        await ThrowIfUnsuccessfulAsync(response, credential, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var page = Deserialize<RawgSearchResponse>(body);
        if (page is null)
        {
            return [];
        }

        var candidates = new List<RawgCandidate>(page.Results.Count);
        foreach (var result in page.Results)
        {
            var name = result.Name;
            if (result.Id is not { } rawgGameId || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            candidates.Add(ToCandidate(rawgGameId, name, result));
        }

        return candidates;
    }

    public async Task ValidateKeyAsync(RawgCredential credential, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            GenresRoute,
            [("key", credential.ApiKey), ("page_size", ValidateKeyPageSize.ToString(CultureInfo.InvariantCulture))],
            cancellationToken);
        await ThrowIfUnsuccessfulAsync(response, credential, cancellationToken);
    }

    public async Task<RawgGameDetailResponse?> FetchDetailAsync(
        int rawgGameId,
        RawgCredential credential,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            $"games/{rawgGameId.ToString(CultureInfo.InvariantCulture)}",
            [("key", credential.ApiKey)],
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await ThrowIfUnsuccessfulAsync(response, credential, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = Deserialize<RawgGameDetail>(body);
        return detail is null ? null : new RawgGameDetailResponse(detail, body);
    }

    private static T? Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException exc)
        {
            throw new RawgApiException("RAWG returned a body this client could not read.", exc);
        }
    }

    private static RawgCandidate ToCandidate(int rawgGameId, string name, RawgSearchResult result)
    {
        var platformIds = new HashSet<int>(
            result.Platforms.Select(entry => entry.Platform?.Id).OfType<int>());

        return new RawgCandidate(
            rawgGameId,
            name,
            platformIds,
            result.Released,
            result.Metacritic,
            result.EsrbRating);
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

    private static async Task<string?> ProviderDetailAsync(
        HttpResponseMessage response,
        RawgCredential credential,
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
        return text.Length > MaxProviderDetailChars ? text[..MaxProviderDetailChars] + "..." : text;
    }

    private static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        RawgCredential credential,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new RawgApiException(
            (int)response.StatusCode,
            RetryAfterSeconds(response),
            await ProviderDetailAsync(response, credential, cancellationToken));
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path,
        IReadOnlyList<(string Key, string Value)> query,
        CancellationToken cancellationToken)
    {
        var queryString = string.Join(
            '&',
            query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var relative = $"{path}?{queryString}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress, relative));
        return await _httpClient.SendAsync(request, cancellationToken);
    }
}
