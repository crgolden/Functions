namespace Functions.Curator.Catalog;

using System.Text.RegularExpressions;

public static class FranchiseAssigner
{
    private static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromSeconds(5);

    public static string? AssignFranchise(string name, IReadOnlyList<FranchiseRule> rules)
    {
        var lower = name.ToLowerInvariant();
        var match = rules
            .OrderBy(rule => rule.Priority)
            .FirstOrDefault(rule => Regex.IsMatch(lower, rule.Pattern, RegexOptions.None, PatternMatchTimeout));
        return match?.Franchise;
    }

    public static string FingerprintFranchiseRules(IReadOnlyList<FranchiseRule> rules)
    {
        var canonical = rules
            .OrderBy(rule => rule.RuleId.ToString(), StringComparer.Ordinal)
            .ThenBy(rule => rule.Pattern, StringComparer.Ordinal)
            .ThenBy(rule => rule.Franchise, StringComparer.Ordinal)
            .ThenBy(rule => rule.Priority)
            .Select(rule => new[]
            {
                CurationRuleFingerprint.PythonJsonString(rule.RuleId.ToString()),
                CurationRuleFingerprint.PythonJsonString(rule.Pattern),
                CurationRuleFingerprint.PythonJsonString(rule.Franchise),
                CurationRuleFingerprint.PythonJsonNumber(rule.Priority),
            })
            .ToArray();
        return CurationRuleFingerprint.Compute(canonical);
    }
}
