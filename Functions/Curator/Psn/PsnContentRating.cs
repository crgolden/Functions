namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnContentRating
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("authority")]
    public string? Authority { get; init; }
}
