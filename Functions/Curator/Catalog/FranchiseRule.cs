namespace Functions.Curator.Catalog;

public sealed record FranchiseRule(Guid RuleId, string Pattern, string Franchise, int Priority);
