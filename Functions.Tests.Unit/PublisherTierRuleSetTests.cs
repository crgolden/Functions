namespace Functions.Tests.Unit;

using Functions.Curator.Enrichment;

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
        var pattern = Generated.LowercaseToken(5);
        var publisherNameContainingThePattern = $"{pattern} {Generated.LowercaseToken(8)}";
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Generated.NewPublisherTierRuleId(), pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
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
        var shorterPattern = Generated.LowercaseToken(4);
        var longerPatternContainingIt = $"{shorterPattern}{Generated.LowercaseToken(2)}";
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(
                Generated.NewPublisherTierRuleId(),
                longerPatternContainingIt,
                PublisherTierRuleSet.AaTier,
                PublisherTierRuleSet.SubstringMatchKind),
            new(
                Generated.NewPublisherTierRuleId(),
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
        var pattern = Generated.NewTokenFromFirstHalfOfAlphabet(7);
        var publisherNameSharingNoCharactersWithIt = Generated.NewTokenFromSecondHalfOfAlphabet(12);
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Generated.NewPublisherTierRuleId(), pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
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
        var pattern = Generated.LowercaseToken(2);
        var publisherNameMerelyStartingWithThePattern = $"{pattern}{Generated.LowercaseToken(9)}";
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Generated.NewPublisherTierRuleId(), pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.ExactMatchKind),
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
        var pattern = Generated.LowercaseToken(5);
        var uppercasedPattern = pattern.ToUpperInvariant();
        var lowercasePublisherNameContainingIt = $"{pattern} {Generated.LowercaseToken(8)}";
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Generated.NewPublisherTierRuleId(), uppercasedPattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier(lowercasePublisherNameContainingIt);

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
    }
}
