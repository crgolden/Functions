namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnReleaseDate
{
    [JsonPropertyName("date")]
    public DateTimeOffset? Date { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}
