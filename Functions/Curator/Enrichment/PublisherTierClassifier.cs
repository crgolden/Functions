namespace Functions.Curator.Enrichment;

public static class PublisherTierClassifier
{
    public static string FingerprintPublisherTierRules(IReadOnlyList<PublisherTierRule> rules)
    {
        var canonical = rules
            .Select(rule => new
            {
                TierId = rule.TierId.ToString(),
                rule.Pattern,
                rule.Tier,
                rule.MatchKind,
            })
            .OrderBy(rule => rule.TierId, StringComparer.Ordinal)
            .ThenBy(rule => rule.Pattern, StringComparer.Ordinal)
            .ThenBy(rule => rule.Tier, StringComparer.Ordinal)
            .ThenBy(rule => rule.MatchKind, StringComparer.Ordinal)
            .Select(rule => new[]
            {
                CurationRuleFingerprint.PythonJsonString(rule.TierId),
                CurationRuleFingerprint.PythonJsonString(rule.Pattern),
                CurationRuleFingerprint.PythonJsonString(rule.Tier),
                CurationRuleFingerprint.PythonJsonString(rule.MatchKind),
            })
            .ToArray();
        return CurationRuleFingerprint.Compute(canonical);
    }
}
