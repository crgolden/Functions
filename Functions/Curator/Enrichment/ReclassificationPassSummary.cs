namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;

public sealed record ReclassificationPassSummary(
    [property: JsonPropertyName(EnrichmentRunSummaryFields.Status)] string Status,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.UpdatedCount)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? UpdatedCount = null);
