namespace Functions.Curator.Enrichment;

public sealed record PublisherTierRule(Guid TierId, string Pattern, string Tier, string MatchKind);
