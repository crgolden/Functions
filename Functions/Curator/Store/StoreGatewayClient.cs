namespace Functions.Curator.Store;

using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

public sealed class StoreGatewayClient : IStoreGatewayClient
{
    internal const string ProductOperation = "metGetProductById";
    internal const string StarRatingOperation = "wcaProductStarRatingRetrive";
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

    private readonly HttpClient _httpClient;

    private readonly StoreGatewaySettings _settings;

    public StoreGatewayClient(HttpClient httpClient, StoreGatewaySettings settings)
    {
        _httpClient = httpClient;
        _settings = settings;
    }

    public async Task<StoreProductNode?> ProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(ProductOperation, _settings.ProductHash, productId, cancellationToken).ConfigureAwait(false);
        return response.Data?.ProductRetrieve;
    }

    public async Task<StoreStarRating?> StarRatingAsync(string productId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(StarRatingOperation, _settings.StarRatingHash, productId, cancellationToken).ConfigureAwait(false);
        return response.Data?.ProductRetrieve?.StarRating;
    }

    public async Task<StoreCategoryGrid> CategoryPageAsync(
        string categoryId,
        int offset,
        int size,
        CancellationToken cancellationToken = default)
    {
        StoreQueryRotatedException? rotated = null;
        foreach (var hash in _settings.CategoryGridHashes)
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

    private Uri CategoryGridUri(string sha256Hash, string categoryId, int offset, int size)
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
            _settings.OperationEndpoint.AbsoluteUri,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OperationNameQueryKey] = CategoryGridOperation,
                [VariablesQueryKey] = variables,
                [ExtensionsQueryKey] = extensions,
            }));
    }

    private Uri OperationUri(string operation, string sha256Hash, string productId)
    {
        var variables = JsonSerializer.Serialize(new { productId });
        var extensions = JsonSerializer.Serialize(new { persistedQuery = new { version = 1, sha256Hash } });
        return new Uri(QueryHelpers.AddQueryString(
            _settings.OperationEndpoint.AbsoluteUri,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OperationNameQueryKey] = operation,
                [VariablesQueryKey] = variables,
                [ExtensionsQueryKey] = extensions,
            }));
    }

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
