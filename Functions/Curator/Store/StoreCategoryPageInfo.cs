namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreCategoryPageInfo
{
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("isLast")]
    public bool IsLast { get; init; }
}
