namespace Functions.Curator.Store;

public interface IStoreGatewayClient
{
    Task<StoreProductNode?> ProductAsync(string productId, CancellationToken cancellationToken = default);

    Task<StoreStarRating?> StarRatingAsync(string productId, CancellationToken cancellationToken = default);
}
