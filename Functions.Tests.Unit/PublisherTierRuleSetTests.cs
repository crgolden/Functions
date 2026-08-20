namespace Functions.Tests.Unit;

using Curator.Enrichment;

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
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "sony", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier("Sony Interactive Entertainment");

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
    }

    [Fact]
    public void ClassifyTier_MatchingBothAaaAndAaRules_PrefersAaa()
    {
        // Arrange
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "team17", PublisherTierRuleSet.AaTier, PublisherTierRuleSet.SubstringMatchKind),
            new(Guid.Parse("b827608b-9c19-7e96-73d5-a5cdcadffed0"), "team", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier("Team17");

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
    }

    [Fact]
    public void ClassifyTier_MatchingNoRule_DefaultsToIndie()
    {
        // Arrange
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "ubisoft", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier("Tiny Indie Studio");

        // Assert
        Assert.Equal(PublisherTierRuleSet.IndieTier, tier);
    }

    [Fact]
    public void ClassifyTier_ExactMatchKind_RequiresTheWholeNameNotASubstring()
    {
        // Arrange
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "ea", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.ExactMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier("Electronic Arts");

        // Assert
        Assert.Equal(PublisherTierRuleSet.IndieTier, tier);
    }

    [Fact]
    public void ClassifyTier_MatchingRuleCasedDifferentlyThanTheName_StillMatches()
    {
        // Arrange
        var ruleSet = PublisherTierRuleSet.Prepare(
        [
            new(Guid.Parse("a4e72a6a-013a-ecbb-b420-cbd3130683fb"), "SONY", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
        ]);

        // Act
        var tier = ruleSet.ClassifyTier("Sony Interactive Entertainment");

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
    }
}
