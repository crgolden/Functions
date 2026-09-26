namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreStarRating
{
    [JsonPropertyName("averageRating")]
    public double? AverageRating { get; init; }

    [JsonPropertyName("totalRatingsCount")]
    public int? TotalRatingsCount { get; init; }
}
