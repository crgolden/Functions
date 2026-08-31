namespace Functions.Tests.Unit;

using Curator.Catalog;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class FranchiseAssignerTests
{
    [Fact]
    public void AssignFranchise_WithNoMatchingRule_ReturnsNull()
    {
        var keyword = TestValues.NewTokenFromFirstHalfOfAlphabet(6);
        var titleSharingNoCharactersWithTheKeyword = TestValues.NewTokenFromSecondHalfOfAlphabet(10);
        var unmatchedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), keyword, unmatchedFranchise, TestValues.NewRulePriority()),
        };

        var franchise = FranchiseAssigner.AssignFranchise(titleSharingNoCharactersWithTheKeyword, rules);

        Assert.Null(franchise);
    }

    [Fact]
    public void AssignFranchise_ReturnsTheLowestPriorityMatchingRule()
    {
        var broadKeyword = TestValues.LowercaseToken(7);
        var narrowKeyword = $"{TestValues.LowercaseToken(5)} {broadKeyword}";
        var winningPriority = TestValues.NewRulePriority();
        var losingPriority = winningPriority + 1;
        var narrowFranchise = TestValues.NewFranchiseName();
        var broadFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), broadKeyword, broadFranchise, losingPriority),
            new(Guid.NewGuid(), narrowKeyword, narrowFranchise, winningPriority),
        };
        var titleMatchingBothRules = $"{narrowKeyword} {TestValues.LowercaseToken(6)}";

        var franchise = FranchiseAssigner.AssignFranchise(titleMatchingBothRules, rules);

        Assert.Equal(narrowFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_LowercasesTheTitleSoLowercaseStoredPatternsMatch()
    {
        var lowercaseKeyword = TestValues.LowercaseToken(6);
        var expectedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), lowercaseKeyword, expectedFranchise, TestValues.NewRulePriority()),
        };

        var uppercaseTitleContainingTheKeyword =
            $"{lowercaseKeyword.ToUpperInvariant()} {TestValues.LowercaseToken(7).ToUpperInvariant()}";

        var franchise = FranchiseAssigner.AssignFranchise(uppercaseTitleContainingTheKeyword, rules);

        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithAYearAnchoredPattern_SkipsATitleWhoseWordIsNotFollowedByAYear()
    {
        var word = TestValues.LowercaseToken(4);
        var yearAnchoredPattern = $@"\b{word} \d{{4}}\b";
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), yearAnchoredPattern, TestValues.NewFranchiseName(), TestValues.NewRulePriority()),
        };
        var titleWhoseWordIsFollowedByASubtitleNotAYear =
            $"{word.ToUpperInvariant()}: {TestValues.LowercaseToken(9)}";

        var franchise = FranchiseAssigner.AssignFranchise(titleWhoseWordIsFollowedByASubtitleNotAYear, rules);

        Assert.Null(franchise);
    }

    [Fact]
    public void AssignFranchise_WithAYearAnchoredPattern_StillMatchesATitleCarryingAFourDigitYear()
    {
        var word = TestValues.LowercaseToken(4);
        var yearAnchoredPattern = $@"\b{word} \d{{4}}\b";
        var expectedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), yearAnchoredPattern, expectedFranchise, TestValues.NewRulePriority()),
        };
        var titleWhoseWordIsFollowedByAFourDigitYear = $"{word} {TestValues.NewReleaseYear()}";

        var franchise = FranchiseAssigner.AssignFranchise(titleWhoseWordIsFollowedByAFourDigitYear, rules);

        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithoutATrailingWordBoundary_MatchesATitleThatContinuesPastThePattern()
    {
        var firstWord = TestValues.LowercaseToken(3);
        var secondWord = TestValues.LowercaseToken(2);
        var patternWithoutATrailingBoundary = $@"\b{firstWord} {secondWord}";
        var expectedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), patternWithoutATrailingBoundary, expectedFranchise, TestValues.NewRulePriority()),
        };
        var titleThatContinuesPastThePattern = $"{firstWord} {secondWord}{TestValues.LowercaseToken(3)}";

        var franchise = FranchiseAssigner.AssignFranchise(titleThatContinuesPastThePattern, rules);

        Assert.Equal(expectedFranchise, franchise);
    }

    [Fact]
    public void AssignFranchise_WithTheOptionalSeparatorPattern_MatchesATitleWrittenWithAnUnderscore()
    {
        var left = TestValues.LowercaseToken(5);
        var right = TestValues.LowercaseToken(4);
        var optionalSeparatorPattern = $"{left}.?{right}";
        var expectedFranchise = TestValues.NewFranchiseName();
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), optionalSeparatorPattern, expectedFranchise, TestValues.NewRulePriority()),
        };
        var titleWrittenWithAnUnderscoreSeparator = $"{left}_{right}{TestValues.LowercaseToken(2)}";

        var franchise = FranchiseAssigner.AssignFranchise(titleWrittenWithAnUnderscoreSeparator, rules);

        Assert.Equal(expectedFranchise, franchise);
    }
}
