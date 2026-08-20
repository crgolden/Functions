namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;

public sealed record EnrichmentRunSummary(
    [property: JsonPropertyName("opencritic_cache_refresh")] OpenCriticRefreshPassSummary OpenCriticCacheRefresh,
    [property: JsonPropertyName("franchise_reclassification")] ReclassificationPassSummary FranchiseReclassification,
    [property: JsonPropertyName("tier_reclassification")] ReclassificationPassSummary TierReclassification,
    [property: JsonPropertyName("enrichment")] EnrichmentPassSummary Enrichment);

public sealed record OpenCriticRefreshPassSummary(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("games_fetched")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? GamesFetched = null,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail = null,
    [property: JsonPropertyName("retry_after_seconds")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? RetryAfterSeconds = null);

public sealed record ReclassificationPassSummary(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("updated_count")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? UpdatedCount = null);

public sealed record EnrichmentPassSummary(
    [property: JsonPropertyName("providers")] IReadOnlyDictionary<string, string> Providers,
    [property: JsonPropertyName("attempted_count")] int AttemptedCount,
    [property: JsonPropertyName("enriched_count")] int EnrichedCount,
    [property: JsonPropertyName("remaining_count")] int RemainingCount,
    [property: JsonPropertyName("stopped_provider")] string? StoppedProvider,
    [property: JsonPropertyName("stopped_reason")] string? StoppedReason,
    [property: JsonPropertyName("rejected_providers")] IReadOnlyList<string> RejectedProviders,
    [property: JsonPropertyName("unavailable_providers")] IReadOnlyList<string> UnavailableProviders);
