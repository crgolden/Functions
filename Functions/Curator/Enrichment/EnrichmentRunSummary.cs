namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;

public sealed record EnrichmentRunSummary(
    [property: JsonPropertyName(EnrichmentRunSummaryFields.OpenCriticCacheRefresh)] OpenCriticRefreshPassSummary OpenCriticCacheRefresh,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.FranchiseReclassification)] ReclassificationPassSummary FranchiseReclassification,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.TierReclassification)] ReclassificationPassSummary TierReclassification,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.Enrichment)] EnrichmentPassSummary Enrichment);
