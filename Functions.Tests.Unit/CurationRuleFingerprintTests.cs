namespace Functions.Tests.Unit;

using Curator;
using Curator.Catalog;
using Curator.Enrichment;

[Trait("Category", "Unit")]
public sealed class CurationRuleFingerprintTests
{
    private const string PythonEmptyRuleListDigest =
        "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945";

    [Fact]
    public void FingerprintFranchiseRules_MatchesTheDigestCuratorsPythonPassAlreadyStored()
    {
        // Arrange
        var rules = new List<FranchiseRule>
        {
            new(Guid.Parse("b0000000-0000-0000-0000-000000000002"), @"\banno \d{4}\b", "Anno", 10),
            new(Guid.Parse("a0000000-0000-0000-0000-000000000001"), "final fantasy( vii)?+", "Final Fantasy", 9),
            new(Guid.Parse("c0000000-0000-0000-0000-000000000003"), "pok\u00e9mon <legends> & 'more'=1", "Pok\u00e9mon", 1),
        };

        // Act
        var fingerprint = FranchiseAssigner.FingerprintFranchiseRules(rules);

        // Assert
        Assert.Equal("05c25c408e704486e912342d8fa2d048cd5a3ef96174111e7db6c848cb6f577a", fingerprint);
    }

    [Fact]
    public void FingerprintPublisherTierRules_MatchesTheDigestCuratorsPythonPassAlreadyStored()
    {
        // Arrange
        var rules = new List<PublisherTierRule>
        {
            new(Guid.Parse("b0000000-0000-0000-0000-000000000002"), "electronic arts", "AAA", "contains"),
            new(Guid.Parse("a0000000-0000-0000-0000-000000000001"), "devolver+digital <indie> & 'co'=1", "AA", "exact"),
            new(Guid.Parse("c0000000-0000-0000-0000-000000000003"), "ubisoft \u00e9ditions", "AAA", "contains"),
        };

        // Act
        var fingerprint = PublisherTierClassifier.FingerprintPublisherTierRules(rules);

        // Assert
        Assert.Equal("8e945100a714f8e91eabd1ec1b8ff0bc62fedf719e5c38288d79906ea2000076", fingerprint);
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
        // Act
        var encoded = CurationRuleFingerprint.PythonJsonString("a+b <c> & 'd'=e");

        // Assert
        Assert.Equal("\"a+b <c> & 'd'=e\"", encoded);
    }

    [Fact]
    public void PythonJsonString_EscapesControlAndHighCharactersAsLowercaseFourDigitHex()
    {
        // Act
        var encoded = CurationRuleFingerprint.PythonJsonString("\u007f\u0001\u00e9");

        // Assert
        Assert.Equal("\"\\u007f\\u0001\\u00e9\"", encoded);
    }

    [Fact]
    public void PythonJsonString_EscapesQuotesBackslashesAndTheNamedControlCharacters()
    {
        // Act
        var encoded = CurationRuleFingerprint.PythonJsonString("\"\\\b\f\n\r\t");

        // Assert
        Assert.Equal("\"\\\"\\\\\\b\\f\\n\\r\\t\"", encoded);
    }

    [Fact]
    public void Compute_SeparatesItemsWithACommaAndASpaceLikePythonsJsonDumpsDefault()
    {
        // Arrange
        string[][] canonical =
        [
            [
                CurationRuleFingerprint.PythonJsonString("\u007f\u0001"),
                CurationRuleFingerprint.PythonJsonString("a"),
                CurationRuleFingerprint.PythonJsonString("b"),
                CurationRuleFingerprint.PythonJsonNumber(1),
            ],
        ];

        // Act
        var fingerprint = CurationRuleFingerprint.Compute(canonical);

        // Assert
        Assert.Equal("1236d188d1cb623a52776c6aaa3fff0ad987dad75a6f99d1dafc62ff63dcf1da", fingerprint);
    }
}
