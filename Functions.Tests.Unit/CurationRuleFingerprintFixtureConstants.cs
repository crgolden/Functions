namespace Functions.Tests.Unit;

internal static class CurationRuleFingerprintFixtureConstants
{
    internal const string PythonEmptyRuleListDigest = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945";

    internal const string PythonFranchiseRulesDigest = "05c25c408e704486e912342d8fa2d048cd5a3ef96174111e7db6c848cb6f577a";
    internal const string PythonFranchiseRuleOneId = "b0000000-0000-0000-0000-000000000002";
    internal const string PythonFranchiseRuleOnePattern = @"\banno \d{4}\b";
    internal const string PythonFranchiseRuleOneFranchise = "Anno";
    internal const int PythonFranchiseRuleOnePriority = 10;
    internal const string PythonFranchiseRuleTwoId = "a0000000-0000-0000-0000-000000000001";
    internal const string PythonFranchiseRuleTwoPattern = "final fantasy( vii)?+";
    internal const string PythonFranchiseRuleTwoFranchise = "Final Fantasy";
    internal const int PythonFranchiseRuleTwoPriority = 9;
    internal const string PythonFranchiseRuleThreeId = "c0000000-0000-0000-0000-000000000003";
    internal const string PythonFranchiseRuleThreePattern = "pokémon <legends> & 'more'=1";
    internal const string PythonFranchiseRuleThreeFranchise = "Pokémon";
    internal const int PythonFranchiseRuleThreePriority = 1;

    internal const string PythonPublisherTierRulesDigest = "8e945100a714f8e91eabd1ec1b8ff0bc62fedf719e5c38288d79906ea2000076";
    internal const string PythonPublisherTierRuleOneId = "b0000000-0000-0000-0000-000000000002";
    internal const string PythonPublisherTierRuleOnePattern = "electronic arts";
    internal const string PythonPublisherTierRuleOneTier = "AAA";
    internal const string PythonPublisherTierRuleOneMatchKind = "contains";
    internal const string PythonPublisherTierRuleTwoId = "a0000000-0000-0000-0000-000000000001";
    internal const string PythonPublisherTierRuleTwoPattern = "devolver+digital <indie> & 'co'=1";
    internal const string PythonPublisherTierRuleTwoTier = "AA";
    internal const string PythonPublisherTierRuleTwoMatchKind = "exact";
    internal const string PythonPublisherTierRuleThreeId = "c0000000-0000-0000-0000-000000000003";
    internal const string PythonPublisherTierRuleThreePattern = "ubisoft éditions";
    internal const string PythonPublisherTierRuleThreeTier = "AAA";
    internal const string PythonPublisherTierRuleThreeMatchKind = "contains";

    internal const string PythonSeparatorDigest = "1236d188d1cb623a52776c6aaa3fff0ad987dad75a6f99d1dafc62ff63dcf1da";
    internal const string PythonSeparatorFirstItem = "";
    internal const string PythonSeparatorSecondItem = "a";
    internal const string PythonSeparatorThirdItem = "b";
    internal const int PythonSeparatorFourthItem = 1;

    internal const string JsonNamedEscapeInput = "\"\\\b\f\n\r\t";
    internal const string PythonNamedEscapeOutput = "\"\\\"\\\\\\b\\f\\n\\r\\t\"";

    internal const string PythonUnescapedPunctuation = "+<>&'=";
    internal const string JsonUnicodeEscapePrefix = @"\u";
    internal const string JsonStringQuote = "\"";
}
