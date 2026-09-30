namespace Functions.Curator.Store;

public sealed record StoreGatewaySettings(
    Uri OperationEndpoint,
    IReadOnlyList<string> CategoryGridHashes,
    string ProductHash,
    string StarRatingHash);
