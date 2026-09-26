namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreLocalizedGenre
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}
