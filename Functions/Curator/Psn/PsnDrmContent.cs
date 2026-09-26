namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnDrmContent
{
    [JsonPropertyName("contentSize")]
    public long? ContentSize { get; init; }
}
