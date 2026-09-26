namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreGraphError
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("extensions")]
    public StoreGraphErrorExtensions? Extensions { get; init; }
}
