namespace Functions.Tests.Unit;

using Curator.Catalog;

[Trait("Category", "Unit")]
public sealed class ExclusionRulesTests
{
    [Fact]
    public void ShouldExclude_ReturnsFalse_WhenNoRuleMatches()
    {
        // Arrange
        var rules = new[] { new ExclusionRule(Guid.Parse("10000000-0000-0000-0000-000000000000"), ExclusionRules.F2pTitle, "Fortnite") };

        // Act
        var excluded = ExclusionRules.ShouldExclude("Bloodborne", rules);

        // Assert
        Assert.False(excluded);
    }

    [Fact]
    public void ShouldExclude_ReturnsTrue_ForAnExactFreeToPlayTitle()
    {
        // Arrange
        var rules = new[] { new ExclusionRule(Guid.Parse("10000000-0000-0000-0000-000000000000"), ExclusionRules.F2pTitle, "Fortnite") };

        // Act
        var excluded = ExclusionRules.ShouldExclude("Fortnite", rules);

        // Assert
        Assert.True(excluded);
    }

    [Fact]
    public void ShouldExclude_LetsAWhitelistedTitleThrough_EvenWhenAnotherRuleMatchesIt()
    {
        // Arrange
        var rules = new[]
        {
            new ExclusionRule(Guid.Parse("10000000-0000-0000-0000-000000000000"), ExclusionRules.F2pTitle, "Fortnite"),
            new ExclusionRule(Guid.Parse("20000000-0000-0000-0000-000000000000"), ExclusionRules.Whitelist, "Fortnite"),
        };

        // Act
        var excluded = ExclusionRules.ShouldExclude("Fortnite", rules);

        // Assert
        Assert.False(excluded);
    }

    [Theory]
    [InlineData("Netflix")]
    [InlineData("Netflix Premium")]
    [InlineData("Netflix: Originals")]
    public void ShouldExclude_ReturnsTrue_ForAMediaAppNameOrOneOfItsSeparatedSuffixes(string name)
    {
        // Arrange
        var rules = new[] { new ExclusionRule(Guid.Parse("10000000-0000-0000-0000-000000000000"), ExclusionRules.MediaApp, "Netflix") };

        // Act
        var excluded = ExclusionRules.ShouldExclude(name, rules);

        // Assert
        Assert.True(excluded);
    }

    [Fact]
    public void ShouldExclude_ReturnsFalse_ForATitleMerelyStartingWithAMediaAppNameAndNoSeparator()
    {
        // Arrange
        var rules = new[] { new ExclusionRule(Guid.Parse("10000000-0000-0000-0000-000000000000"), ExclusionRules.MediaApp, "Netflix") };

        // Act
        var excluded = ExclusionRules.ShouldExclude("Netflixed Adventures", rules);

        // Assert
        Assert.False(excluded);
    }

    [Fact]
    public void ShouldExclude_MatchesANamePatternAgainstTheLowercasedTitle()
    {
        // Arrange
        var rules = new[] { new ExclusionRule(Guid.Parse("10000000-0000-0000-0000-000000000000"), ExclusionRules.NamePattern, "demo$") };

        // Act
        var excluded = ExclusionRules.ShouldExclude("Some Game DEMO", rules);

        // Assert
        Assert.True(excluded);
    }

    [Fact]
    public void ShouldExclude_SearchesAnywhereInTheName_RatherThanAnchoringAtTheStart()
    {
        // Arrange
        var rules = new[] { new ExclusionRule(Guid.Parse("10000000-0000-0000-0000-000000000000"), ExclusionRules.NamePattern, "trial") };

        // Act
        var excluded = ExclusionRules.ShouldExclude("Game Trial Edition", rules);

        // Assert
        Assert.True(excluded);
    }
}
