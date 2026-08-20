namespace Functions.Curator.Enrichment;

public sealed class PublisherTierRuleSet
{
    public const string AaaTier = "AAA";
    public const string AaTier = "AA";
    public const string IndieTier = "Indie";

    internal const string ExactMatchKind = "exact";

    internal const string SubstringMatchKind = "substring";

    private readonly IReadOnlyList<(string Pattern, bool Exact)> _aaa;
    private readonly IReadOnlyList<(string Pattern, bool Exact)> _aa;

    private PublisherTierRuleSet(
        IReadOnlyList<(string Pattern, bool Exact)> aaa,
        IReadOnlyList<(string Pattern, bool Exact)> aa)
    {
        _aaa = aaa;
        _aa = aa;
    }

    public static PublisherTierRuleSet Prepare(IReadOnlyList<PublisherTierRule> rules) =>
        new(PreparedFor(rules, AaaTier), PreparedFor(rules, AaTier));

    public string? ClassifyTier(string? publisherOrDeveloper)
    {
        if (string.IsNullOrWhiteSpace(publisherOrDeveloper))
        {
            return null;
        }

        var lower = publisherOrDeveloper.ToLowerInvariant();
        if (_aaa.Any(rule => Matches(rule, lower)))
        {
            return AaaTier;
        }

        return _aa.Any(rule => Matches(rule, lower)) ? AaTier : IndieTier;
    }

    private static bool Matches((string Pattern, bool Exact) rule, string loweredValue) =>
        rule.Exact
            ? string.Equals(rule.Pattern, loweredValue, StringComparison.Ordinal)
            : loweredValue.Contains(rule.Pattern, StringComparison.Ordinal);

    private static List<(string Pattern, bool Exact)> PreparedFor(
        IReadOnlyList<PublisherTierRule> rules,
        string tier) =>
        [.. rules
            .Where(rule => string.Equals(rule.Tier, tier, StringComparison.Ordinal))
            .Select(rule => (
                rule.Pattern.ToLowerInvariant(),
                string.Equals(rule.MatchKind, ExactMatchKind, StringComparison.Ordinal)))];
}
