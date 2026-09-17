namespace Functions.Tests.Unit;

using Curator.Rawg;
using TestSupport;
using static RawgMatcherFixtureConstants;
using static TestSupport.TestValues;

[Trait("Category", "Unit")]
public sealed class RawgMatcherTests
{
    [Theory]
    [InlineData('™')]
    [InlineData('®')]
    [InlineData('©')]
    public void Normalize_StripsTrademarkRegisteredAndCopyrightSigns(char sign)
    {
        // Arrange
        var title = TestValues.NewGameTitle();

        // Act
        var normalized = RawgMatcher.Normalize($"{title}{sign}");

        // Assert
        Assert.Equal(title, normalized);
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('’')]
    [InlineData('‘')]
    [InlineData('ʼ')]
    [InlineData('`')]
    public void Normalize_FoldsEveryTypographicApostropheOntoTheAsciiOne(char apostrophe)
    {
        // Arrange
        var owner = TestValues.LowercaseToken(6);
        var possessiveLetter = TestValues.LowercaseToken(1);
        var possession = TestValues.LowercaseToken(8);

        // Act
        var normalized = RawgMatcher.Normalize($"{owner}{apostrophe}{possessiveLetter} {possession}");

        // Assert
        Assert.Equal($"{owner}{AsciiApostrophe}{possessiveLetter} {possession}", normalized);
    }

    [Fact]
    public void Normalize_ProducesTheSameKeyForTheAsciiAndTypographicSpellingOfThePossessive()
    {
        // Arrange
        var owner = TestValues.LowercaseToken(6);
        var possessiveLetter = TestValues.LowercaseToken(1);
        var possession = TestValues.LowercaseToken(8);
        var ascii = RawgMatcher.Normalize($"{owner}{AsciiApostrophe}{possessiveLetter} {possession}");

        // Act
        var typographic = RawgMatcher.Normalize($"{owner}{RightSingleQuotationMark}{possessiveLetter} {possession}");

        // Assert
        Assert.Equal(ascii, typographic);
    }

    [Theory]
    [InlineData('–')]
    [InlineData('—')]
    public void Normalize_FoldsEnAndEmDashesOntoAHyphen(char dash)
    {
        // Arrange
        var left = TestValues.LowercaseToken(6);
        var right = TestValues.LowercaseToken(8);

        // Act
        var normalized = RawgMatcher.Normalize($"{left} {dash} {right}");

        // Assert
        Assert.Equal($"{left} - {right}", normalized);
    }

    [Fact]
    public void Normalize_CollapsesRunsOfWhitespaceAndTrimsTheEnds()
    {
        // Arrange
        var first = TestValues.LowercaseToken(6);
        var second = TestValues.LowercaseToken(8);

        // Act
        var normalized = RawgMatcher.Normalize($"  {first}   {second}  ");

        // Assert
        Assert.Equal($"{first} {second}", normalized);
    }

    [Fact]
    public void Normalize_Lowercases()
    {
        // Arrange
        var title = TestValues.NewGameTitle();

        // Act
        var normalized = RawgMatcher.Normalize(title.ToUpperInvariant());

        // Assert
        Assert.Equal(title, normalized);
    }

    [Fact]
    public void Normalize_OfAnEmptyTitleIsEmpty() =>
        Assert.Equal(string.Empty, RawgMatcher.Normalize(string.Empty));

    [Fact]
    public void Similarity_OfTheAsciiAndTypographicPossessiveSpellingIsExactlyOne()
    {
        // Arrange
        var owner = TestValues.LowercaseToken(6);
        var possessiveLetter = TestValues.LowercaseToken(1);
        var possession = TestValues.LowercaseToken(8);

        // Act
        var ratio = RawgMatcher.Similarity(
            $"{owner}{RightSingleQuotationMark}{possessiveLetter} {possession}",
            $"{owner}{AsciiApostrophe}{possessiveLetter} {possession}");

        // Assert
        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void Similarity_IsOrderIndependent()
    {
        // Arrange
        var titleWithSpaces = TestValues.NewGameTitle();
        var sameTitleWithoutSpacesAndUpperCased = WithoutSpacesAndUpperCased(titleWithSpaces);
        var forward = RawgMatcher.Similarity(titleWithSpaces, sameTitleWithoutSpacesAndUpperCased);

        // Act
        var backward = RawgMatcher.Similarity(sameTitleWithoutSpacesAndUpperCased, titleWithSpaces);

        // Assert
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Similarity_OfCompletelyUnrelatedTitlesIsLow()
    {
        // Arrange
        var title = TestValues.NewTokenFromFirstHalfOfAlphabet(10);
        var titleSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(20);

        // Act
        var ratio = RawgMatcher.Similarity(title, titleSharingNoCharactersWithIt);

        // Assert
        Assert.True(ratio < RawgMatcher.DefaultMatchThreshold);
    }

    [Fact]
    public void Similarity_OfTwoEmptyTitlesIsOne()
    {
        // Act
        var ratio = RawgMatcher.Similarity(string.Empty, string.Empty);

        // Assert
        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void FindBestMatch_RejectsACandidateCarryingNeitherAPs4NorPs5PlatformId()
    {
        // Arrange
        var title = TestValues.NewGameTitle();
        var candidates = new[]
        {
            new RawgCandidate(NewRawgGameId(), title, new HashSet<int> { PcPlatformId, Ps3PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch(title, candidates);

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_AcceptsACandidateCarryingOnlyThePs4PlatformId()
    {
        // Arrange
        var title = TestValues.NewGameTitle();
        var ps4OnlyId = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(ps4OnlyId, title, new HashSet<int> { RawgMatcher.Ps4PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch(title, candidates);

        // Assert
        Assert.Equal(ps4OnlyId, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_AcceptsACandidateCarryingOnlyThePs5PlatformId()
    {
        // Arrange
        var title = TestValues.NewGameTitle();
        var ps5OnlyId = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(ps5OnlyId, title, new HashSet<int> { RawgMatcher.Ps5PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch(title, candidates);

        // Assert
        Assert.Equal(ps5OnlyId, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_ReturnsNullWhenNoCandidateClearsTheThreshold()
    {
        // Arrange
        var title = TestValues.NewTokenFromFirstHalfOfAlphabet(12);
        var candidateNameSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(20);
        var candidates = new[]
        {
            new RawgCandidate(
                NewRawgGameId(),
                candidateNameSharingNoCharactersWithIt,
                new HashSet<int> { RawgMatcher.Ps5PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch(title, candidates);

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_ReturnsNullForAnEmptyCandidateList()
    {
        // Act
        var match = RawgMatcher.FindBestMatch(TestValues.NewGameTitle(), []);

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_PrefersTheHighestScoringEligibleCandidate()
    {
        // Arrange
        var title = TestValues.NewGameTitle();
        var sameTitleWithAnEditionSuffix = $"{title}: {TestValues.NewGameTitle()}";
        var editionSuffixedId = NewRawgGameId();
        var exactTitleId = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(
                editionSuffixedId,
                sameTitleWithAnEditionSuffix,
                new HashSet<int> { RawgMatcher.Ps4PlatformId }),
            new RawgCandidate(exactTitleId, title, new HashSet<int> { RawgMatcher.Ps4PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch(title, candidates);

        // Assert
        Assert.Equal(exactTitleId, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_IgnoresAnIneligibleCandidateEvenWhenItScoresHigherThanAnEligibleOne()
    {
        // Arrange
        var title = TestValues.NewGameTitle();
        var nearMissTitle = WithoutItsLastCharacter(title);
        var exactlyMatchingPcOnlyId = NewRawgGameId();
        var nearMissPs4Id = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(exactlyMatchingPcOnlyId, title, new HashSet<int> { PcPlatformId }),
            new RawgCandidate(nearMissPs4Id, nearMissTitle, new HashSet<int> { RawgMatcher.Ps4PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch(title, candidates);

        // Assert
        Assert.Equal(nearMissPs4Id, match?.RawgGameId);
    }

    private static string WithoutSpacesAndUpperCased(string title) =>
        string.Concat(title.Split(' ')).ToUpperInvariant();

    private static string WithoutItsLastCharacter(string title) => title[..^1];
}
