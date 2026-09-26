namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;

public sealed record OpenCriticRefreshPassSummary(
    [property: JsonPropertyName(EnrichmentRunSummaryFields.Status)] string Status,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.GamesFetched)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? GamesFetched = null,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.Detail)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail = null,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.RetryAfterSeconds)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? RetryAfterSeconds = null);
