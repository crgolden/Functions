namespace Functions.Curator.Enrichment;

using JetBrains.Annotations;

[PublicAPI]
public sealed record RawgCacheEntry(string NormalizedTitle, int? RawgGameId, string? Raw);
