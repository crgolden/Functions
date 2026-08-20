namespace Functions.Curator.Enrichment;

public sealed record EnrichmentBatchResult(
    int EnrichedCount,
    IReadOnlyList<string> RawgEnrichedTitles,
    IReadOnlyList<string> OpenCriticEnrichedTitles,
    EnrichmentProvider? RateLimitedProvider,
    double? RetryAfterSeconds,
    IReadOnlyList<string> RemainingGameIds,
    IReadOnlyList<EnrichmentProvider> RejectedProviders,
    IReadOnlyList<EnrichmentProvider> UnavailableProviders,
    string? StoppedReason = null);
