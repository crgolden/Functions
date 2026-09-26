namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreGraphData
{
    [JsonPropertyName("productRetrieve")]
    public StoreProductNode? ProductRetrieve { get; init; }

    [JsonPropertyName("categoryGridRetrieve")]
    public StoreCategoryGrid? CategoryGridRetrieve { get; init; }
}
