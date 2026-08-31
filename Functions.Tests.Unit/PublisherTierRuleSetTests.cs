namespace Functions.Tests.Unit;

using Curator.Enrichment;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PublisherTierRuleSetTests
{
    [Fact]
    public void ClassifyTier_WithNoName_ReturnsNullRatherThanIndie()
    {
        // Arrange
        var ruleSet = PublisherTierRuleSet.Prepare([]);

        // Act
        var tier = ruleSet.ClassifyTier(null);

        // Assert
        Assert.Null(tier);
    }

    [Fact]
    public void ClassifyTier_MatchingAnAaaRule_ReturnsAaa()
    {
        // Arrange
        var pattern = TestValues.LowercaseToken(5);
        var publisherNameContainingThePattern = $"{pattern} {TestValues.LowercaseToken(8)}";
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.NewGuid(), pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier(publisherNameContainingThePattern);

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
    }

    [Fact]
    public void ClassifyTier_MatchingBothAaaAndAaRules_PrefersAaa()
    {
        // Arrange
        var shorterPattern = TestValues.LowercaseToken(4);
        var longerPatternContainingIt = $"{shorterPattern}{TestValues.LowercaseToken(2)}";
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(
                Guid.NewGuid(),
                longerPatternContainingIt,
                PublisherTierRuleSet.AaTier,
                PublisherTierRuleSet.SubstringMatchKind),
            new(
                Guid.NewGuid(),
                shorterPattern,
                PublisherTierRuleSet.AaaTier,
                PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier(longerPatternContainingIt);

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
    }

    [Fact]
    public void ClassifyTier_MatchingNoRule_DefaultsToIndie()
    {
        // Arrange
        var pattern = TestValues.NewTokenFromFirstHalfOfAlphabet(7);
        var publisherNameSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(12);
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.NewGuid(), pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier(publisherNameSharingNoCharactersWithIt);

        // Assert
        Assert.Equal(PublisherTierRuleSet.IndieTier, tier);
    }

    [Fact]
    public void ClassifyTier_ExactMatchKind_RequiresTheWholeNameNotASubstring()
    {
        // Arrange
        var pattern = TestValues.LowercaseToken(2);
        var publisherNameMerelyStartingWithThePattern = $"{pattern}{TestValues.LowercaseToken(9)}";
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.NewGuid(), pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.ExactMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier(publisherNameMerelyStartingWithThePattern);

        // Assert
        Assert.Equal(PublisherTierRuleSet.IndieTier, tier);
    }

    [Fact]
    public void ClassifyTier_MatchingRuleCasedDifferentlyThanTheName_StillMatches()
    {
        // Arrange
        var pattern = TestValues.LowercaseToken(5);
        var uppercasedPattern = pattern.ToUpperInvariant();
        var lowercasePublisherNameContainingIt = $"{pattern} {TestValues.LowercaseToken(8)}";
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.NewGuid(), uppercasedPattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier(lowercasePublisherNameContainingIt);

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
    }
}
