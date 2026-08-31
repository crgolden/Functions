namespace Functions.Tests.Unit;

using Curator.Catalog;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ExclusionRulesTests
{
    [Fact]
    public void ShouldExclude_ReturnsFalse_WhenNoRuleMatches()
    {
        // Arrange
        var excludedTitle = TestValues.NewTokenFromFirstHalfOfAlphabet(12);
        var titleSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(12);
        var rules = new[] { Rule(ExclusionRules.F2pTitle, excludedTitle) };

        // Act
        var excluded = ExclusionRules.ShouldExclude(titleSharingNoCharactersWithIt, rules);

        // Assert
        Assert.False(excluded);
    }

    [Fact]
    public void ShouldExclude_ReturnsTrue_ForAnExactFreeToPlayTitle()
    {
        // Arrange
        var freeToPlayTitle = TestValues.NewGameTitle();
        var rules = new[] { Rule(ExclusionRules.F2pTitle, freeToPlayTitle) };

        // Act
        var excluded = ExclusionRules.ShouldExclude(freeToPlayTitle, rules);

        // Assert
        Assert.True(excluded);
    }

    [Fact]
    public void ShouldExclude_LetsAWhitelistedTitleThrough_EvenWhenAnotherRuleMatchesIt()
    {
        // Arrange
        var titleOnBothLists = TestValues.NewGameTitle();
        var rules = new[]
        {
            Rule(ExclusionRules.F2pTitle, titleOnBothLists),
            Rule(ExclusionRules.Whitelist, titleOnBothLists),
        };

        // Act
        var excluded = ExclusionRules.ShouldExclude(titleOnBothLists, rules);

        // Assert
        Assert.False(excluded);
    }

    [Fact]
    public void ShouldExclude_ReturnsTrue_ForAnExactMediaAppName()
    {
        // Arrange
        var mediaAppName = TestValues.NewGameTitle();
        var rules = new[] { Rule(ExclusionRules.MediaApp, mediaAppName) };

        // Act
        var excluded = ExclusionRules.ShouldExclude(mediaAppName, rules);

        // Assert
        Assert.True(excluded);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(": ")]
    public void ShouldExclude_ReturnsTrue_ForAMediaAppNameFollowedByASeparatedSuffix(string separator)
    {
        // Arrange
        var mediaAppName = TestValues.NewGameTitle();
        var sameNameWithASeparatedSuffix = $"{mediaAppName}{separator}{TestValues.LowercaseToken(8)}";
        var rules = new[] { Rule(ExclusionRules.MediaApp, mediaAppName) };

        // Act
        var excluded = ExclusionRules.ShouldExclude(sameNameWithASeparatedSuffix, rules);

        // Assert
        Assert.True(excluded);
    }

    [Fact]
    public void ShouldExclude_ReturnsFalse_ForATitleMerelyStartingWithAMediaAppNameAndNoSeparator()
    {
        // Arrange
        var mediaAppName = TestValues.NewGameTitle();
        var sameNameRunningStraightOnIntoMoreLetters =
            $"{mediaAppName}{TestValues.LowercaseToken(6)} {TestValues.LowercaseToken(8)}";
        var rules = new[] { Rule(ExclusionRules.MediaApp, mediaAppName) };

        // Act
        var excluded = ExclusionRules.ShouldExclude(sameNameRunningStraightOnIntoMoreLetters, rules);

        // Assert
        Assert.False(excluded);
    }

    [Fact]
    public void ShouldExclude_MatchesANamePatternAgainstTheLowercasedTitle()
    {
        // Arrange
        var trailingKeyword = TestValues.LowercaseToken(6);
        var patternAnchoredAtTheEnd = $"{trailingKeyword}$";
        var titleEndingInTheKeywordUppercased =
            $"{TestValues.NewGameTitle()} {trailingKeyword.ToUpperInvariant()}";
        var rules = new[] { Rule(ExclusionRules.NamePattern, patternAnchoredAtTheEnd) };

        // Act
        var excluded = ExclusionRules.ShouldExclude(titleEndingInTheKeywordUppercased, rules);

        // Assert
        Assert.True(excluded);
    }

    [Fact]
    public void ShouldExclude_SearchesAnywhereInTheName_RatherThanAnchoringAtTheStart()
    {
        // Arrange
        var keyword = TestValues.LowercaseToken(6);
        var titleCarryingTheKeywordInTheMiddle =
            $"{TestValues.LowercaseToken(5)} {keyword} {TestValues.LowercaseToken(7)}";
        var rules = new[] { Rule(ExclusionRules.NamePattern, keyword) };

        // Act
        var excluded = ExclusionRules.ShouldExclude(titleCarryingTheKeywordInTheMiddle, rules);

        // Assert
        Assert.True(excluded);
    }

    private static ExclusionRule Rule(string ruleType, string pattern) =>
        new(Guid.NewGuid(), ruleType, pattern);
}
