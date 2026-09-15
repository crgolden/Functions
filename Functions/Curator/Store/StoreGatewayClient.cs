namespace Functions.Curator.Store;

using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

public sealed class StoreGatewayClient : IStoreGatewayClient
{
#pragma warning disable S1075 // fixed PSN endpoint, not environment-configurable
    internal const string OperationUrl = "https://web.np.playstation.com/api/graphql/v1/op";
#pragma warning restore S1075

    internal const string ProductOperation = "metGetProductById";
    internal const string ProductHash = "a128042177bd93dd831164103d53b73ef790d56f51dae647064cb8f9d9fc9d1a";
    internal const string StarRatingOperation = "wcaProductStarRatingRetrive";
    internal const string StarRatingHash = "cedd370c39e89da20efa7b2e55710e88cb6e6843cc2f8203f7e73ba4751e7253";
    internal const string PersistedQueryNotFoundCode = "PERSISTED_QUERY_NOT_FOUND";
    internal const string PersistedQueryNotFoundMessage = "PersistedQueryNotFound";
    internal const string LocaleHeaderName = "x-psn-store-locale-override";
    internal const string LocaleHeaderValue = "en-US";
    internal const string PreflightHeaderName = "apollo-require-preflight";
    internal const string PreflightHeaderValue = "true";
    internal const string OperationNameQueryKey = "operationName";
    internal const string VariablesQueryKey = "variables";
    internal const string ExtensionsQueryKey = "extensions";

    private readonly HttpClient _httpClient;

    public StoreGatewayClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<StoreProductNode?> ProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(ProductOperation, ProductHash, productId, cancellationToken).ConfigureAwait(false);
        return response.Data?.ProductRetrieve;
    }

    public async Task<StoreStarRating?> StarRatingAsync(string productId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(StarRatingOperation, StarRatingHash, productId, cancellationToken).ConfigureAwait(false);
        return response.Data?.ProductRetrieve?.StarRating;
    }

    internal static Uri OperationUri(string operation, string sha256Hash, string productId)
    {
        var variables = JsonSerializer.Serialize(new { productId });
        var extensions = JsonSerializer.Serialize(new { persistedQuery = new { version = 1, sha256Hash } });
        return new Uri(QueryHelpers.AddQueryString(
            OperationUrl,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OperationNameQueryKey] = operation,
                [VariablesQueryKey] = variables,
                [ExtensionsQueryKey] = extensions,
            }));
    }

    internal static StoreGraphResponse Parse(string body, string operation)
    {
        StoreGraphResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreGraphResponse>(body);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException(UnusableAnswer(operation), exception);
        }

        return parsed ?? throw new HttpRequestException(UnusableAnswer(operation));
    }

    internal static string UnusableAnswer(string operation) =>
        $"The storefront answered {operation} with a body that is not a GraphQL response, so it said nothing about the product; "
        + "that is the storefront being unusable, not the product being absent.";

    internal static bool IsRotatedQuery(StoreGraphResponse response) =>
        response.Errors.Any(error =>
            string.Equals(error.Extensions?.Code, PersistedQueryNotFoundCode, StringComparison.Ordinal)
            || string.Equals(error.Message, PersistedQueryNotFoundMessage, StringComparison.Ordinal));

    private async Task<StoreGraphResponse> SendAsync(
        string operation,
        string sha256Hash,
        string productId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OperationUri(operation, sha256Hash, productId));
        request.Headers.TryAddWithoutValidation(LocaleHeaderName, LocaleHeaderValue);
        request.Headers.TryAddWithoutValidation(PreflightHeaderName, PreflightHeaderValue);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = Parse(body, operation);
        if (IsRotatedQuery(parsed))
        {
            throw new StoreQueryRotatedException(
                $"The storefront no longer recognises the persisted query for {operation}; refresh its hash from the store site's own traffic.");
        }

        return parsed;
    }
}
