namespace Functions.Curator.Store;

using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

public sealed class StoreGatewayClient : IStoreGatewayClient
{
    internal const string OperationPath = "/api/graphql/v1/op";
    internal const string ProductOperation = "metGetProductById";
    internal const string ProductHash = "a128042177bd93dd831164103d53b73ef790d56f51dae647064cb8f9d9fc9d1a";
    internal const string StarRatingOperation = "wcaProductStarRatingRetrive";
    internal const string StarRatingHash = "cedd370c39e89da20efa7b2e55710e88cb6e6843cc2f8203f7e73ba4751e7253";
    internal const string CategoryGridOperation = "categoryGridRetrieve";
    internal const string FullGameFilter = "storeDisplayClassification:FULL_GAME";
    internal const string SortField = "productReleaseDate";
    internal const string NotWhitelistedFragment = "not whitelisted";
    internal const string OperationNameHeaderName = "x-apollo-operation-name";
    internal const string NoHashConfigured = "No persisted-query hash is configured for categoryGridRetrieve.";
    internal const string PersistedQueryNotFoundCode = "PERSISTED_QUERY_NOT_FOUND";
    internal const string PersistedQueryNotFoundMessage = "PersistedQueryNotFound";
    internal const string LocaleHeaderName = "x-psn-store-locale-override";
    internal const string LocaleHeaderValue = "en-US";
    internal const string PreflightHeaderName = "apollo-require-preflight";
    internal const string PreflightHeaderValue = "true";
    internal const string OperationNameQueryKey = "operationName";
    internal const string VariablesQueryKey = "variables";
    internal const string ExtensionsQueryKey = "extensions";

    internal static readonly string OperationUrl = Uri.UriSchemeHttps + Uri.SchemeDelimiter + WebApiHost + OperationPath;

    internal static readonly string[] CategoryGridHashes =
    [
        "9845afc0dbaab4965f6563fffc703f588c8e76792000e8610843b8d3ee9c4c09",
        "4ce7d4102e888ca5cef646ba4527ce64a45cf7edf3daa03d67e9e1e4c0a81d35",
    ];

    private const string WebApiHost = "web.np.playstation.com";

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

    public async Task<StoreCategoryGrid> CategoryPageAsync(
        string categoryId,
        int offset,
        int size,
        CancellationToken cancellationToken = default)
    {
        StoreQueryRotatedException? rotated = null;
        foreach (var hash in CategoryGridHashes)
        {
            try
            {
                var response = await SendAsync(
                    CategoryGridOperation,
                    CategoryGridUri(hash, categoryId, offset, size),
                    cancellationToken).ConfigureAwait(false);
                return response.Data?.CategoryGridRetrieve
                    ?? throw new HttpRequestException(UnusableAnswer(CategoryGridOperation));
            }
            catch (StoreQueryRotatedException exception)
            {
                rotated = exception;
            }
        }

        throw rotated ?? new StoreQueryRotatedException(NoHashConfigured);
    }

    internal static Uri CategoryGridUri(string sha256Hash, string categoryId, int offset, int size)
    {
        var variables = JsonSerializer.Serialize(new
        {
            id = categoryId,
            pageArgs = new { size, offset },
            sortBy = new { name = SortField, isAscending = true },
            filterBy = new[] { FullGameFilter },
            facetOptions = Array.Empty<string>(),
        });
        var extensions = JsonSerializer.Serialize(new { persistedQuery = new { version = 1, sha256Hash } });
        return new Uri(QueryHelpers.AddQueryString(
            OperationUrl,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OperationNameQueryKey] = CategoryGridOperation,
                [VariablesQueryKey] = variables,
                [ExtensionsQueryKey] = extensions,
            }));
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
        response.Message?.Contains(NotWhitelistedFragment, StringComparison.OrdinalIgnoreCase) == true
        || response.Errors.Any(error =>
            string.Equals(error.Extensions?.Code, PersistedQueryNotFoundCode, StringComparison.Ordinal)
            || string.Equals(error.Message, PersistedQueryNotFoundMessage, StringComparison.Ordinal));

    private Task<StoreGraphResponse> SendAsync(
        string operation,
        string sha256Hash,
        string productId,
        CancellationToken cancellationToken) =>
        SendAsync(operation, OperationUri(operation, sha256Hash, productId), cancellationToken);

    private async Task<StoreGraphResponse> SendAsync(
        string operation,
        Uri operationUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, operationUri);
        request.Headers.TryAddWithoutValidation(LocaleHeaderName, LocaleHeaderValue);
        request.Headers.TryAddWithoutValidation(PreflightHeaderName, PreflightHeaderValue);
        request.Headers.TryAddWithoutValidation(OperationNameHeaderName, operation);
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
