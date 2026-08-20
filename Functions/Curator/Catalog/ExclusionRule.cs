namespace Functions.Curator.Catalog;

public sealed record ExclusionRule(Guid RuleId, string RuleType, string Pattern);
