namespace Functions.Tests.Unit;

using Curator.Rawg;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class RawgMatcherTests
{
    private const int PcPlatformId = 1;
    private const int Ps3PlatformId = 16;

    [Theory]
    [InlineData('™')]
    [InlineData('®')]
    [InlineData('©')]
    public void Normalize_StripsTrademarkRegisteredAndCopyrightSigns(char sign)
    {
        var title = TestValues.NewGameTitle();

        Assert.Equal(title, RawgMatcher.Normalize($"{title}{sign}"));
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('’')]
    [InlineData('‘')]
    [InlineData('ʼ')]
    [InlineData('`')]
    public void Normalize_FoldsEveryTypographicApostropheOntoTheAsciiOne(char apostrophe)
    {
        var owner = TestValues.LowercaseToken(6);
        var possession = TestValues.LowercaseToken(8);

        Assert.Equal($"{owner}'s {possession}", RawgMatcher.Normalize($"{owner}{apostrophe}s {possession}"));
    }

    [Fact]
    public void Normalize_ProducesTheSameKeyForTheAsciiAndTypographicSpellingOfThePossessive()
    {
        var owner = TestValues.LowercaseToken(6);
        var possession = TestValues.LowercaseToken(8);

        var ascii = RawgMatcher.Normalize($"{owner}'s {possession}");
        var typographic = RawgMatcher.Normalize($"{owner}’s {possession}");

        Assert.Equal(ascii, typographic);
    }

    [Theory]
    [InlineData('–')]
    [InlineData('—')]
    public void Normalize_FoldsEnAndEmDashesOntoAHyphen(char dash)
    {
        var left = TestValues.LowercaseToken(6);
        var right = TestValues.LowercaseToken(8);

        Assert.Equal($"{left} - {right}", RawgMatcher.Normalize($"{left} {dash} {right}"));
    }

    [Fact]
    public void Normalize_CollapsesRunsOfWhitespaceAndTrimsTheEnds()
    {
        var first = TestValues.LowercaseToken(6);
        var second = TestValues.LowercaseToken(8);

        Assert.Equal($"{first} {second}", RawgMatcher.Normalize($"  {first}   {second}  "));
    }

    [Fact]
    public void Normalize_Lowercases()
    {
        var title = TestValues.NewGameTitle();

        Assert.Equal(title, RawgMatcher.Normalize(title.ToUpperInvariant()));
    }

    [Fact]
    public void Normalize_OfAnEmptyTitleIsEmpty() =>
        Assert.Equal(string.Empty, RawgMatcher.Normalize(string.Empty));

    [Fact]
    public void Similarity_OfTheAsciiAndTypographicPossessiveSpellingIsExactlyOne()
    {
        var owner = TestValues.LowercaseToken(6);
        var possession = TestValues.LowercaseToken(8);

        var ratio = RawgMatcher.Similarity($"{owner}’s {possession}", $"{owner}'s {possession}");

        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void Similarity_IsOrderIndependent()
    {
        var titleWithSpaces = TestValues.NewGameTitle();
        var sameTitleWithoutSpacesAndUpperCased = WithoutSpacesAndUpperCased(titleWithSpaces);

        var forward = RawgMatcher.Similarity(titleWithSpaces, sameTitleWithoutSpacesAndUpperCased);
        var backward = RawgMatcher.Similarity(sameTitleWithoutSpacesAndUpperCased, titleWithSpaces);

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Similarity_OfCompletelyUnrelatedTitlesIsLow()
    {
        var title = TestValues.NewTokenFromFirstHalfOfAlphabet(10);
        var titleSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(20);

        var ratio = RawgMatcher.Similarity(title, titleSharingNoCharactersWithIt);

        Assert.True(ratio < RawgMatcher.DefaultMatchThreshold);
    }

    [Fact]
    public void Similarity_OfTwoEmptyTitlesIsOne()
    {
        var ratio = RawgMatcher.Similarity(string.Empty, string.Empty);

        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void FindBestMatch_RejectsACandidateCarryingNeitherAPs4NorPs5PlatformId()
    {
        var title = TestValues.NewGameTitle();
        var candidates = new[]
        {
            new RawgCandidate(NewRawgGameId(), title, new HashSet<int> { PcPlatformId, Ps3PlatformId }),
        };

        var match = RawgMatcher.FindBestMatch(title, candidates);

        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_AcceptsACandidateCarryingOnlyThePs4PlatformId()
    {
        var title = TestValues.NewGameTitle();
        var ps4OnlyId = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(ps4OnlyId, title, new HashSet<int> { RawgMatcher.Ps4PlatformId }),
        };

        var match = RawgMatcher.FindBestMatch(title, candidates);

        Assert.Equal(ps4OnlyId, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_AcceptsACandidateCarryingOnlyThePs5PlatformId()
    {
        var title = TestValues.NewGameTitle();
        var ps5OnlyId = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(ps5OnlyId, title, new HashSet<int> { RawgMatcher.Ps5PlatformId }),
        };

        var match = RawgMatcher.FindBestMatch(title, candidates);

        Assert.Equal(ps5OnlyId, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_ReturnsNullWhenNoCandidateClearsTheThreshold()
    {
        var title = TestValues.NewTokenFromFirstHalfOfAlphabet(12);
        var candidateNameSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(20);
        var candidates = new[]
        {
            new RawgCandidate(
                NewRawgGameId(),
                candidateNameSharingNoCharactersWithIt,
                new HashSet<int> { RawgMatcher.Ps5PlatformId }),
        };

        var match = RawgMatcher.FindBestMatch(title, candidates);

        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_ReturnsNullForAnEmptyCandidateList()
    {
        var match = RawgMatcher.FindBestMatch(TestValues.NewGameTitle(), []);

        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_PrefersTheHighestScoringEligibleCandidate()
    {
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

        var match = RawgMatcher.FindBestMatch(title, candidates);

        Assert.Equal(exactTitleId, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_IgnoresAnIneligibleCandidateEvenWhenItScoresHigherThanAnEligibleOne()
    {
        var title = TestValues.NewGameTitle();
        var nearMissTitle = WithoutItsLastCharacter(title);
        var exactlyMatchingPcOnlyId = NewRawgGameId();
        var nearMissPs4Id = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(exactlyMatchingPcOnlyId, title, new HashSet<int> { PcPlatformId }),
            new RawgCandidate(nearMissPs4Id, nearMissTitle, new HashSet<int> { RawgMatcher.Ps4PlatformId }),
        };

        var match = RawgMatcher.FindBestMatch(title, candidates);

        Assert.Equal(nearMissPs4Id, match?.RawgGameId);
    }

    private static string WithoutSpacesAndUpperCased(string title) =>
        title.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static string WithoutItsLastCharacter(string title) => title[..^1];

    private static int NewRawgGameId() => TestValues.NewRawgGameId();
}
