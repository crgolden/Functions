namespace Functions.Curator.Enrichment;

public sealed record RawgCacheEntry(string NormalizedTitle, int? RawgGameId, string? Raw);
