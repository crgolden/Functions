namespace Functions.Curator.Enrichment;

public sealed record EnrichmentCandidate(string GameId, string Title, string? ProductId, string? TitleId, bool IsPs5);
