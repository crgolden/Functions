namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnConceptImage
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}
