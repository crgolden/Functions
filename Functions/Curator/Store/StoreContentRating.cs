namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreContentRating
{
    [JsonPropertyName("authority")]
    public string? Authority { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
