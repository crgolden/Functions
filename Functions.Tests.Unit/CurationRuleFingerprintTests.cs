namespace Functions.Tests.Unit;

using System.Globalization;
using Curator;
using Curator.Catalog;
using Curator.Enrichment;
using TestSupport;
using static CurationRuleFingerprintFixtureConstants;

[Trait("Category", "Unit")]
public sealed class CurationRuleFingerprintTests
{
    [Fact]
    public void FingerprintFranchiseRules_MatchesTheDigestCuratorsPythonPassAlreadyStored()
    {
        // Arrange
        var rules = new List<FranchiseRule>
        {
            new(Guid.Parse(PythonFranchiseRuleOneId), PythonFranchiseRuleOnePattern, PythonFranchiseRuleOneFranchise, PythonFranchiseRuleOnePriority),
            new(Guid.Parse(PythonFranchiseRuleTwoId), PythonFranchiseRuleTwoPattern, PythonFranchiseRuleTwoFranchise, PythonFranchiseRuleTwoPriority),
            new(Guid.Parse(PythonFranchiseRuleThreeId), PythonFranchiseRuleThreePattern, PythonFranchiseRuleThreeFranchise, PythonFranchiseRuleThreePriority),
        };

        // Act
        var fingerprint = FranchiseAssigner.FingerprintFranchiseRules(rules);

        // Assert
        Assert.Equal(PythonFranchiseRulesDigest, fingerprint);
    }

    [Fact]
    public void FingerprintPublisherTierRules_MatchesTheDigestCuratorsPythonPassAlreadyStored()
    {
        // Arrange
        var rules = new List<PublisherTierRule>
        {
            new(Guid.Parse(PythonPublisherTierRuleOneId), PythonPublisherTierRuleOnePattern, PythonPublisherTierRuleOneTier, PythonPublisherTierRuleOneMatchKind),
            new(Guid.Parse(PythonPublisherTierRuleTwoId), PythonPublisherTierRuleTwoPattern, PythonPublisherTierRuleTwoTier, PythonPublisherTierRuleTwoMatchKind),
            new(Guid.Parse(PythonPublisherTierRuleThreeId), PythonPublisherTierRuleThreePattern, PythonPublisherTierRuleThreeTier, PythonPublisherTierRuleThreeMatchKind),
        };

        // Act
        var fingerprint = PublisherTierClassifier.FingerprintPublisherTierRules(rules);

        // Assert
        Assert.Equal(PythonPublisherTierRulesDigest, fingerprint);
    }

    [Fact]
    public void FingerprintFranchiseRules_WithNoRules_MatchesPythonsEmptyListDigest()
    {
        // Act
        var fingerprint = FranchiseAssigner.FingerprintFranchiseRules([]);

        // Assert
        Assert.Equal(PythonEmptyRuleListDigest, fingerprint);
    }

    [Fact]
    public void FingerprintPublisherTierRules_WithNoRules_MatchesPythonsEmptyListDigest()
    {
        // Act
        var fingerprint = PublisherTierClassifier.FingerprintPublisherTierRules([]);

        // Assert
        Assert.Equal(PythonEmptyRuleListDigest, fingerprint);
    }

    [Fact]
    public void PythonJsonString_LeavesTheCharactersPythonNeverEscapesAsLiterals()
    {
        // Arrange
        var unescaped = string.Concat(
            Enumerable.Range(0, PythonUnescapedPunctuation.Length)
                .Select(index => $"{TestValues.LowercaseToken(1)}{PythonUnescapedPunctuation[index]}"));

        // Act
        var encoded = CurationRuleFingerprint.PythonJsonString(unescaped);

        // Assert
        Assert.Equal(Quoted(unescaped), encoded);
    }

    [Fact]
    public void PythonJsonString_EscapesAnUnnamedControlCharacterAsLowercaseFourDigitHex()
    {
        // Arrange
        var controlCodePoint = Random.Shared.Next(0x0e, 0x20);

        // Act
        var encoded = CurationRuleFingerprint.PythonJsonString(new string((char)controlCodePoint, 1));

        // Assert
        Assert.Equal(Quoted(UnicodeEscape(controlCodePoint)), encoded);
    }

    [Fact]
    public void PythonJsonString_EscapesTheDeleteCharacter_ThoughItIsAscii()
    {
        // Arrange
        const int deleteCodePoint = 0x7f;

        // Act
        var encoded = CurationRuleFingerprint.PythonJsonString(new string((char)deleteCodePoint, 1));

        // Assert
        Assert.Equal(Quoted(UnicodeEscape(deleteCodePoint)), encoded);
    }

    [Fact]
    public void PythonJsonString_EscapesANonAsciiCharacterAsLowercaseFourDigitHex()
    {
        // Arrange
        var hexLetterCodePoint = Random.Shared.Next(0xe0, 0xf0);

        // Act
        var encoded = CurationRuleFingerprint.PythonJsonString(new string((char)hexLetterCodePoint, 1));

        // Assert
        Assert.Equal(Quoted(UnicodeEscape(hexLetterCodePoint)), encoded);
    }

    [Fact]
    public void PythonJsonString_EscapesQuotesBackslashesAndTheNamedControlCharacters()
    {
        // Act
        var encoded = CurationRuleFingerprint.PythonJsonString(JsonNamedEscapeInput);

        // Assert
        Assert.Equal(PythonNamedEscapeOutput, encoded);
    }

    [Fact]
    public void Compute_SeparatesItemsWithACommaAndASpaceLikePythonsJsonDumpsDefault()
    {
        // Arrange
        string[][] canonical =
        [
            [
                CurationRuleFingerprint.PythonJsonString(PythonSeparatorFirstItem),
                CurationRuleFingerprint.PythonJsonString(PythonSeparatorSecondItem),
                CurationRuleFingerprint.PythonJsonString(PythonSeparatorThirdItem),
                CurationRuleFingerprint.PythonJsonNumber(PythonSeparatorFourthItem),
            ],
        ];

        // Act
        var fingerprint = CurationRuleFingerprint.Compute(canonical);

        // Assert
        Assert.Equal(PythonSeparatorDigest, fingerprint);
    }

    private static string Quoted(string value) => $"{JsonStringQuote}{value}{JsonStringQuote}";

    private static string UnicodeEscape(int codePoint) =>
        $"{JsonUnicodeEscapePrefix}{codePoint.ToString("x4", CultureInfo.InvariantCulture)}";
}
