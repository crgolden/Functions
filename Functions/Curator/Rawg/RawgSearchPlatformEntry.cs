namespace Functions.Curator.Rawg;

using System.Text.Json.Serialization;

public sealed record RawgSearchPlatformEntry
{
    [JsonPropertyName("platform")]
    public RawgSearchPlatform? Platform { get; init; }
}
