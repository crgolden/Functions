namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreConcept
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
