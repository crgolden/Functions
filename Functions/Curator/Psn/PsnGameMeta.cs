namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnGameMeta
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("packageType")]
    public string? PackageType { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("iconUrl")]
    public Uri? IconUrl { get; init; }
}
