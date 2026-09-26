namespace Functions.Curator.Psn;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record PsnCompatibilityNotice
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }
}
