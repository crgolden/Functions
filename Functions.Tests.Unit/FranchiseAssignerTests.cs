namespace Functions.Tests.Unit;

using Functions.Curator.Catalog;
using static Functions.Tests.Unit.RegexSyntaxFixtureConstants;

[Trait("Category", "Unit")]
public sealed class FranchiseAssignerTests
{
    [Fact]
    public void AssignFranchise_WithNoMatchingRule_ReturnsNull()
    {
        // Arrange
        var keyword = Generated.NewTokenFromFirstHalfOfAlphabet(6);
        var titleSharingNoCharactersWithTheKeyword = Generated.NewTokenFromSecondHalfOfAlphabet(10);
        var unmatchedFranchise = Generated.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Generated.NewFranchiseRuleId(), keyword, unmatchedFranchise, Generated.NewRulePriority()),
        };

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleSharingNoCharactersWithTheKeyword, rules);

        // Assert
        Assert.Null(franchise);
    }

    [Fact]
    public void AssignFranchise_ReturnsTheLowestPriorityMatchingRule()
    {
        // Arrange
        var broadKeyword = Generated.LowercaseToken(7);
        var narrowKeyword = $"{Generated.LowercaseToken(5)} {broadKeyword}";
        var winningPriority = Generated.NewRulePriority();
        var losingPriority = winningPriority + 1;
        var narrowFranchise = Generated.NewFranchiseName();
        var broadFranchise = Generated.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Generated.NewFranchiseRuleId(), broadKeyword, broadFranchise, losingPriority),
            new(Generated.NewFranchiseRuleId(), narrowKeyword, narrowFranchise, winningPriority),
        };
        var titleMatchingBothRules = $"{narrowKeyword} {Generated.LowercaseToken(6)}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleMatchingBothRules, rules);

        // Assert
        Assert.Equal(narrowFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_LowercasesTheTitleSoLowercaseStoredPatternsMatch()
    {
        // Arrange
        var lowercaseKeyword = Generated.LowercaseToken(6);
        var expectedFranchise = Generated.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Generated.NewFranchiseRuleId(), lowercaseKeyword, expectedFranchise, Generated.NewRulePriority()),
        };

        var uppercaseTitleContainingTheKeyword =
            $"{lowercaseKeyword.ToUpperInvariant()} {Generated.LowercaseToken(7).ToUpperInvariant()}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(uppercaseTitleContainingTheKeyword, rules);

        // Assert
        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithAYearAnchoredPattern_SkipsATitleWhoseWordIsNotFollowedByAYear()
    {
        // Arrange
        var word = Generated.LowercaseToken(4);
        var yearAnchoredPattern = $"{WordBoundary}{word} {FourDigitRun}{WordBoundary}";
        var rules = new List<FranchiseRule>
        {
            new(Generated.NewFranchiseRuleId(), yearAnchoredPattern, Generated.NewFranchiseName(), Generated.NewRulePriority()),
        };
        var titleWhoseWordIsFollowedByASubtitleNotAYear =
            $"{word.ToUpperInvariant()}: {Generated.LowercaseToken(9)}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleWhoseWordIsFollowedByASubtitleNotAYear, rules);

        // Assert
        Assert.Null(franchise);
    }

    [Fact]
    public void AssignFranchise_WithAYearAnchoredPattern_StillMatchesATitleCarryingAFourDigitYear()
    {
        // Arrange
        var word = Generated.LowercaseToken(4);
        var yearAnchoredPattern = $"{WordBoundary}{word} {FourDigitRun}{WordBoundary}";
        var expectedFranchise = Generated.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Generated.NewFranchiseRuleId(), yearAnchoredPattern, expectedFranchise, Generated.NewRulePriority()),
        };
        var titleWhoseWordIsFollowedByAFourDigitYear = $"{word} {Generated.NewReleaseYear()}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleWhoseWordIsFollowedByAFourDigitYear, rules);

        // Assert
        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithoutATrailingWordBoundary_MatchesATitleThatContinuesPastThePattern()
    {
        // Arrange
        var firstWord = Generated.LowercaseToken(3);
        var secondWord = Generated.LowercaseToken(2);
        var patternWithoutATrailingBoundary = $"{WordBoundary}{firstWord} {secondWord}";
        var expectedFranchise = Generated.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Generated.NewFranchiseRuleId(), patternWithoutATrailingBoundary, expectedFranchise, Generated.NewRulePriority()),
        };
        var titleThatContinuesPastThePattern = $"{firstWord} {secondWord}{Generated.LowercaseToken(3)}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleThatContinuesPastThePattern, rules);

        // Assert
        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithTheOptionalSeparatorPattern_MatchesATitleWrittenWithAnUnderscore()
    {
        // Arrange
        var left = Generated.LowercaseToken(5);
        var right = Generated.LowercaseToken(4);
        var optionalSeparatorPattern = $"{left}.?{right}";
        var expectedFranchise = Generated.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Generated.NewFranchiseRuleId(), optionalSeparatorPattern, expectedFranchise, Generated.NewRulePriority()),
        };
        var titleWrittenWithAnUnderscoreSeparator = $"{left}_{right}{Generated.LowercaseToken(2)}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleWrittenWithAnUnderscoreSeparator, rules);

        // Assert
        Assert.Equal(expectedFranchise, franchise);
    }
}
