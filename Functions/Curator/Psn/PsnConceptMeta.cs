namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnConceptMeta
{
    [JsonPropertyName("conceptId")]
    public string? ConceptId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("iconUrl")]
    [JsonConverter(typeof(AbsoluteUriConverter))]
    public Uri? IconUrl { get; init; }
}
