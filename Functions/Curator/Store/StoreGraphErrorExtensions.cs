namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreGraphErrorExtensions
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }
}
