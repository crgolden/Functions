namespace Functions.Curator.Enrichment;

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
