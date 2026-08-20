namespace Functions.Tests.Unit;

using Curator.Rawg;

[Trait("Category", "Unit")]
public sealed class RawgMatcherTests
{
    private const int PcPlatformId = 1;
    private const int Ps3PlatformId = 16;

    [Theory]
    [InlineData("Horizon Forbidden West™", "horizon forbidden west")]
    [InlineData("WipEout® Omega Collection", "wipeout omega collection")]
    [InlineData("Assassin's Creed© Valhalla", "assassin's creed valhalla")]
    public void Normalize_StripsTrademarkRegisteredAndCopyrightSigns(string title, string expected) =>
        Assert.Equal(expected, RawgMatcher.Normalize(title));

    [Theory]
    [InlineData("Marvel's Spider-Man 2", "marvel's spider-man 2")]
    [InlineData("Marvel’s Spider-Man 2", "marvel's spider-man 2")]
    [InlineData("Marvel‘s Spider-Man 2", "marvel's spider-man 2")]
    [InlineData("Marvelʼs Spider-Man 2", "marvel's spider-man 2")]
    [InlineData("Marvel`s Spider-Man 2", "marvel's spider-man 2")]
    public void Normalize_FoldsEveryTypographicApostropheOntoTheAsciiOne(string title, string expected) =>
        Assert.Equal(expected, RawgMatcher.Normalize(title));

    [Fact]
    public void Normalize_ProducesTheSameKeyForTheAsciiAndTypographicSpellingOfThePossessive()
    {
        // Arrange
        var ascii = RawgMatcher.Normalize("Demon's Souls");
        var typographic = RawgMatcher.Normalize("Demon’s Souls");

        // Assert
        Assert.Equal(ascii, typographic);
    }

    [Theory]
    [InlineData("Grand Theft Auto III – The Definitive Edition", "grand theft auto iii - the definitive edition")]
    [InlineData("Nioh 2 — The Complete Edition", "nioh 2 - the complete edition")]
    public void Normalize_FoldsEnAndEmDashesOntoAHyphen(string title, string expected) =>
        Assert.Equal(expected, RawgMatcher.Normalize(title));

    [Theory]
    [InlineData("  Spaced   Out   Title  ", "spaced out title")]
    [InlineData("UPPERCASE TITLE", "uppercase title")]
    [InlineData("", "")]
    public void Normalize_CollapsesWhitespaceTrimsAndLowercases(string title, string expected) =>
        Assert.Equal(expected, RawgMatcher.Normalize(title));

    [Fact]
    public void Similarity_OfTheAsciiAndTypographicPossessiveSpellingIsExactlyOne()
    {
        // Act
        var ratio = RawgMatcher.Similarity("Demon’s Souls", "Demon's Souls");

        // Assert
        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void Similarity_IsOrderIndependent()
    {
        // Act
        var forward = RawgMatcher.Similarity("Bloodborne", "Blood Borne");
        var backward = RawgMatcher.Similarity("Blood Borne", "Bloodborne");

        // Assert
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Similarity_OfCompletelyUnrelatedTitlesIsLow()
    {
        // Act
        var ratio = RawgMatcher.Similarity("Bloodborne", "Farming Simulator 22");

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
        var candidates = new[]
        {
            new RawgCandidate(1, "Bloodborne", new HashSet<int> { PcPlatformId, Ps3PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch("Bloodborne", candidates);

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_AcceptsACandidateCarryingOnlyThePs4PlatformId()
    {
        // Arrange
        var candidates = new[]
        {
            new RawgCandidate(1, "Bloodborne", new HashSet<int> { RawgMatcher.Ps4PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch("Bloodborne", candidates);

        // Assert
        Assert.Equal(1, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_AcceptsACandidateCarryingOnlyThePs5PlatformId()
    {
        // Arrange
        var candidates = new[]
        {
            new RawgCandidate(1, "Bloodborne", new HashSet<int> { RawgMatcher.Ps5PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch("Bloodborne", candidates);

        // Assert
        Assert.Equal(1, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_ReturnsNullWhenNoCandidateClearsTheThreshold()
    {
        // Arrange
        var candidates = new[]
        {
            new RawgCandidate(1, "Completely Different Game", new HashSet<int> { RawgMatcher.Ps5PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch("Bloodborne", candidates);

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_ReturnsNullForAnEmptyCandidateList()
    {
        // Act
        var match = RawgMatcher.FindBestMatch("Bloodborne", []);

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void FindBestMatch_PrefersTheHighestScoringEligibleCandidate()
    {
        // Arrange
        var editionSuffixedId = NewRawgGameId();
        var exactTitleId = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(
                editionSuffixedId,
                "Bloodborne: Game of the Year Edition",
                new HashSet<int> { RawgMatcher.Ps4PlatformId }),
            new RawgCandidate(exactTitleId, "Bloodborne", new HashSet<int> { RawgMatcher.Ps4PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch("Bloodborne", candidates);

        // Assert
        Assert.Equal(exactTitleId, match?.RawgGameId);
    }

    [Fact]
    public void FindBestMatch_IgnoresAnIneligibleCandidateEvenWhenItScoresHigherThanAnEligibleOne()
    {
        // Arrange
        var betterScoringPcOnlyId = NewRawgGameId();
        var eligiblePs4Id = NewRawgGameId();
        var candidates = new[]
        {
            new RawgCandidate(betterScoringPcOnlyId, "Bloodborne", new HashSet<int> { PcPlatformId }),
            new RawgCandidate(eligiblePs4Id, "Bloodborn", new HashSet<int> { RawgMatcher.Ps4PlatformId }),
        };

        // Act
        var match = RawgMatcher.FindBestMatch("Bloodborne", candidates);

        // Assert
        Assert.Equal(eligiblePs4Id, match?.RawgGameId);
    }

    private static int NewRawgGameId() => Random.Shared.Next(1, 1_000_000);
}
