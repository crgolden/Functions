namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnConceptMeta
{
    [JsonPropertyName("conceptId")]
    public string? ConceptId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; init; }
}
