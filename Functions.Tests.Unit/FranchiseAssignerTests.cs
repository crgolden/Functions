namespace Functions.Tests.Unit;

using Curator.Catalog;
using TestSupport;
using static RegexSyntaxFixtureConstants;

[Trait("Category", "Unit")]
public sealed class FranchiseAssignerTests
{
    [Fact]
    public void AssignFranchise_WithNoMatchingRule_ReturnsNull()
    {
        // Arrange
        var keyword = TestValues.NewTokenFromFirstHalfOfAlphabet(6);
        var titleSharingNoCharactersWithTheKeyword = TestValues.NewTokenFromSecondHalfOfAlphabet(10);
        var unmatchedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(TestValues.NewFranchiseRuleId(), keyword, unmatchedFranchise, TestValues.NewRulePriority()),
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
        var broadKeyword = TestValues.LowercaseToken(7);
        var narrowKeyword = $"{TestValues.LowercaseToken(5)} {broadKeyword}";
        var winningPriority = TestValues.NewRulePriority();
        var losingPriority = winningPriority + 1;
        var narrowFranchise = TestValues.NewFranchiseName();
        var broadFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(TestValues.NewFranchiseRuleId(), broadKeyword, broadFranchise, losingPriority),
            new(TestValues.NewFranchiseRuleId(), narrowKeyword, narrowFranchise, winningPriority),
        };
        var titleMatchingBothRules = $"{narrowKeyword} {TestValues.LowercaseToken(6)}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleMatchingBothRules, rules);

        // Assert
        Assert.Equal(narrowFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_LowercasesTheTitleSoLowercaseStoredPatternsMatch()
    {
        // Arrange
        var lowercaseKeyword = TestValues.LowercaseToken(6);
        var expectedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(TestValues.NewFranchiseRuleId(), lowercaseKeyword, expectedFranchise, TestValues.NewRulePriority()),
        };

        var uppercaseTitleContainingTheKeyword =
            $"{lowercaseKeyword.ToUpperInvariant()} {TestValues.LowercaseToken(7).ToUpperInvariant()}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(uppercaseTitleContainingTheKeyword, rules);

        // Assert
        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithAYearAnchoredPattern_SkipsATitleWhoseWordIsNotFollowedByAYear()
    {
        // Arrange
        var word = TestValues.LowercaseToken(4);
        var yearAnchoredPattern = $"{WordBoundary}{word} {FourDigitRun}{WordBoundary}";
        var rules = new List<FranchiseRule>
        {
            new(TestValues.NewFranchiseRuleId(), yearAnchoredPattern, TestValues.NewFranchiseName(), TestValues.NewRulePriority()),
        };
        var titleWhoseWordIsFollowedByASubtitleNotAYear =
            $"{word.ToUpperInvariant()}: {TestValues.LowercaseToken(9)}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleWhoseWordIsFollowedByASubtitleNotAYear, rules);

        // Assert
        Assert.Null(franchise);
    }

    [Fact]
    public void AssignFranchise_WithAYearAnchoredPattern_StillMatchesATitleCarryingAFourDigitYear()
    {
        // Arrange
        var word = TestValues.LowercaseToken(4);
        var yearAnchoredPattern = $"{WordBoundary}{word} {FourDigitRun}{WordBoundary}";
        var expectedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(TestValues.NewFranchiseRuleId(), yearAnchoredPattern, expectedFranchise, TestValues.NewRulePriority()),
        };
        var titleWhoseWordIsFollowedByAFourDigitYear = $"{word} {TestValues.NewReleaseYear()}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleWhoseWordIsFollowedByAFourDigitYear, rules);

        // Assert
        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithoutATrailingWordBoundary_MatchesATitleThatContinuesPastThePattern()
    {
        // Arrange
        var firstWord = TestValues.LowercaseToken(3);
        var secondWord = TestValues.LowercaseToken(2);
        var patternWithoutATrailingBoundary = $"{WordBoundary}{firstWord} {secondWord}";
        var expectedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(TestValues.NewFranchiseRuleId(), patternWithoutATrailingBoundary, expectedFranchise, TestValues.NewRulePriority()),
        };
        var titleThatContinuesPastThePattern = $"{firstWord} {secondWord}{TestValues.LowercaseToken(3)}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleThatContinuesPastThePattern, rules);

        // Assert
        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithTheOptionalSeparatorPattern_MatchesATitleWrittenWithAnUnderscore()
    {
        // Arrange
        var left = TestValues.LowercaseToken(5);
        var right = TestValues.LowercaseToken(4);
        var optionalSeparatorPattern = $"{left}.?{right}";
        var expectedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(TestValues.NewFranchiseRuleId(), optionalSeparatorPattern, expectedFranchise, TestValues.NewRulePriority()),
        };
        var titleWrittenWithAnUnderscoreSeparator = $"{left}_{right}{TestValues.LowercaseToken(2)}";

        // Act
        var franchise = FranchiseAssigner.AssignFranchise(titleWrittenWithAnUnderscoreSeparator, rules);

        // Assert
        Assert.Equal(expectedFranchise, franchise);
    }
}
