namespace Functions.Curator.Rawg;

using System.Text.Json.Serialization;

public sealed record RawgNamed
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
