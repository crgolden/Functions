namespace Functions.Curator.Enrichment;

public sealed record GameEnrichmentSignals(
    int? ReleaseYear,
    string? Developer,
    string? Publisher,
    string? Esrb,
    bool? Multiplayer,
    double? CriticalScore,
    double? OcScore,
    string? OcTier,
    double? OcPercentRecommended,
    double? PsnRating,
    string? ScoreSource,
    string? AaaTier,
    bool RawgEnriched,
    bool OpencriticEnriched);
