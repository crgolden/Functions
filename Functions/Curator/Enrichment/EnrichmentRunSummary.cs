namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;

public static class EnrichmentRunSummaryFields
{
    public const string OpenCriticCacheRefresh = "opencritic_cache_refresh";
    public const string FranchiseReclassification = "franchise_reclassification";
    public const string TierReclassification = "tier_reclassification";
    public const string Enrichment = "enrichment";
    public const string Status = "status";
    public const string GamesFetched = "games_fetched";
    public const string Detail = "detail";
    public const string RetryAfterSeconds = "retry_after_seconds";
    public const string UpdatedCount = "updated_count";
    public const string Providers = "providers";
    public const string AttemptedCount = "attempted_count";
    public const string EnrichedCount = "enriched_count";
    public const string RemainingCount = "remaining_count";
    public const string StoppedProvider = "stopped_provider";
    public const string StoppedReason = "stopped_reason";
    public const string RejectedProviders = "rejected_providers";
    public const string UnavailableProviders = "unavailable_providers";
    public const string RawgEnrichedCount = "rawg_enriched_count";
    public const string OpenCriticEnrichedCount = "opencritic_enriched_count";
    public const string PsnEnrichedCount = "psn_enriched_count";
}

public sealed record EnrichmentRunSummary(
    [property: JsonPropertyName(EnrichmentRunSummaryFields.OpenCriticCacheRefresh)] OpenCriticRefreshPassSummary OpenCriticCacheRefresh,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.FranchiseReclassification)] ReclassificationPassSummary FranchiseReclassification,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.TierReclassification)] ReclassificationPassSummary TierReclassification,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.Enrichment)] EnrichmentPassSummary Enrichment);

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

public sealed record ReclassificationPassSummary(
    [property: JsonPropertyName(EnrichmentRunSummaryFields.Status)] string Status,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.UpdatedCount)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? UpdatedCount = null);

public sealed record EnrichmentPassSummary(
    [property: JsonPropertyName(EnrichmentRunSummaryFields.Providers)] IReadOnlyDictionary<string, string> Providers,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.AttemptedCount)] int AttemptedCount,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.EnrichedCount)] int EnrichedCount,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.RemainingCount)] int RemainingCount,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.StoppedProvider)] string? StoppedProvider,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.StoppedReason)] string? StoppedReason,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.RejectedProviders)] IReadOnlyList<string> RejectedProviders,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.UnavailableProviders)] IReadOnlyList<string> UnavailableProviders,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.RawgEnrichedCount)] int RawgEnrichedCount = 0,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.OpenCriticEnrichedCount)] int OpenCriticEnrichedCount = 0,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.PsnEnrichedCount)] int PsnEnrichedCount = 0);
