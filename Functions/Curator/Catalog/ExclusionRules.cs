namespace Functions.Curator.Catalog;

using System.Text.RegularExpressions;

public static class ExclusionRules
{
    public const string Whitelist = "whitelist";
    public const string F2pTitle = "f2p_title";
    public const string MediaApp = "media_app";
    public const string NamePattern = "name_pattern";

    private static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(100);

    public static bool ShouldExclude(string name, IReadOnlyList<ExclusionRule> rules)
    {
        if (rules.Any(rule => rule.RuleType == Whitelist && rule.Pattern == name))
        {
            return false;
        }

        if (rules.Any(rule => rule.RuleType == F2pTitle && rule.Pattern == name))
        {
            return true;
        }

        var mediaApps = rules.Where(rule => rule.RuleType == MediaApp).Select(rule => rule.Pattern);
        if (mediaApps.Any(app =>
                name == app
                || name.StartsWith($"{app} ", StringComparison.Ordinal)
                || name.StartsWith($"{app}:", StringComparison.Ordinal)))
        {
            return true;
        }

        var lower = name.ToLowerInvariant();
        return rules
            .Where(rule => rule.RuleType == NamePattern)
            .Any(rule => Regex.IsMatch(lower, rule.Pattern, RegexOptions.None, PatternMatchTimeout));
    }
}
