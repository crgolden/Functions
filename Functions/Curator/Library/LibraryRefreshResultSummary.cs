namespace Functions.Curator.Library;

using System.Text.Json.Serialization;

public sealed record LibraryRefreshResultSummary
{
    [JsonPropertyName("rawg_enriched_titles")]
    public IReadOnlyList<string> RawgEnrichedTitles { get; init; } = [];

    [JsonPropertyName("opencritic_enriched_titles")]
    public IReadOnlyList<string> OpenCriticEnrichedTitles { get; init; } = [];

    [JsonPropertyName("opencritic_topup_incomplete")]
    public bool OpenCriticTopupIncomplete { get; init; }

    [JsonPropertyName("rejected_providers")]
    public IReadOnlyList<string> RejectedProviders { get; init; } = [];

    [JsonPropertyName("unavailable_providers")]
    public IReadOnlyList<string> UnavailableProviders { get; init; } = [];
}

public sealed record LibraryRefreshContinuationSummary
{
    [JsonPropertyName("rawg_enriched_titles")]
    public IReadOnlyList<string> RawgEnrichedTitles { get; init; } = [];

    [JsonPropertyName("opencritic_enriched_titles")]
    public IReadOnlyList<string> OpenCriticEnrichedTitles { get; init; } = [];

    [JsonPropertyName("opencritic_topup_incomplete")]
    public bool OpenCriticTopupIncomplete { get; init; }

    [JsonPropertyName("stopped_reason")]
    required public string StoppedReason { get; init; }

    [JsonPropertyName("rate_limited_provider")]
    public string? RateLimitedProvider { get; init; }

    [JsonPropertyName("retry_after_seconds")]
    public double RetryAfterSeconds { get; init; }

    [JsonPropertyName("remaining_count")]
    public int RemainingCount { get; init; }

    [JsonPropertyName("rejected_providers")]
    public IReadOnlyList<string> RejectedProviders { get; init; } = [];

    [JsonPropertyName("unavailable_providers")]
    public IReadOnlyList<string> UnavailableProviders { get; init; } = [];
}
