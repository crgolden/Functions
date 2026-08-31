namespace Functions.Tests.Unit;

using Curator.OpenCritic;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class OpenCriticNameIndexTests
{
    [Theory]
    [InlineData("II", "2")]
    [InlineData("III", "3")]
    [InlineData("IV", "4")]
    [InlineData("VI", "6")]
    [InlineData("VII", "7")]
    [InlineData("VIII", "8")]
    [InlineData("IX", "9")]
    [InlineData("V", "v")]
    [InlineData("X", "x")]
    [InlineData("I", "i")]
    public void Normalize_ConvertsOnlyTheRomanNumeralsCuratorActuallyMaps(string numeral, string expected)
    {
        var precedingWords = NewFillerWords();

        Assert.Equal(
            $"{precedingWords} {expected}",
            OpenCriticNameIndex.Normalize($"{precedingWords} {numeral}"));
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('’')]
    [InlineData('‘')]
    [InlineData('ʼ')]
    [InlineData('`')]
    public void Normalize_RemovesTypographicApostrophesTheSameAsAsciiOnes(char apostrophe)
    {
        var owner = TestValues.LowercaseToken(6);
        var possession = TestValues.LowercaseToken(7);

        Assert.Equal(
            $"{owner}s {possession}",
            OpenCriticNameIndex.Normalize($"{owner}{apostrophe}s {possession}"));
    }

    [Fact]
    public void Normalize_LeavesTmGluedToTheWordBecauseCompatibilityDecompositionRunsFirst()
    {
        var word = TestValues.LowercaseToken(8);

        Assert.Equal($"{word}tm", OpenCriticNameIndex.Normalize($"{word}™"));
    }

    [Theory]
    [InlineData('®')]
    [InlineData('©')]
    public void Normalize_StripsRegisteredAndCopyrightSigns(char sign)
    {
        var word = TestValues.LowercaseToken(8);

        Assert.Equal(word, OpenCriticNameIndex.Normalize($"{word}{sign}"));
    }

    [Theory]
    [InlineData("(tm)")]
    [InlineData("(r)")]
    [InlineData("(c)")]
    public void Normalize_StripsTheParenthesisedMarkForms(string mark)
    {
        var word = TestValues.LowercaseToken(8);

        Assert.Equal(word, OpenCriticNameIndex.Normalize($"{word} {mark}"));
    }

    [Theory]
    [InlineData('é', 'e')]
    [InlineData('è', 'e')]
    [InlineData('á', 'a')]
    [InlineData('ü', 'u')]
    public void Normalize_FoldsAnAccentedLetterOntoItsBaseLetter(char accented, char folded)
    {
        var before = TestValues.LowercaseToken(4);
        var after = TestValues.LowercaseToken(5);

        Assert.Equal(
            $"{before}{folded}{after}",
            OpenCriticNameIndex.Normalize($"{before}{accented}{after}"));
    }

    [Theory]
    [InlineData('²', '2')]
    [InlineData('⁴', '4')]
    public void Normalize_FoldsASuperscriptDigitOntoItsAsciiForm(char superscript, char digit)
    {
        var word = TestValues.LowercaseToken(8);

        Assert.Equal($"{word}{digit}", OpenCriticNameIndex.Normalize($"{word}{superscript}"));
    }

    [Fact]
    public void Normalize_FoldsTheNumeroSignOntoNo()
    {
        var word = TestValues.LowercaseToken(8);

        Assert.Equal($"no {word}", OpenCriticNameIndex.Normalize($"№ {word}"));
    }

    [Theory]
    [InlineData('–')]
    [InlineData('—')]
    [InlineData('-')]
    [InlineData(':')]
    [InlineData('&')]
    [InlineData('.')]
    [InlineData('/')]
    public void Normalize_ReplacesASeparatorWithASingleSpace(char separator)
    {
        var left = TestValues.LowercaseToken(6);
        var right = TestValues.LowercaseToken(7);

        Assert.Equal(
            $"{left} {right}",
            OpenCriticNameIndex.Normalize($"{left}{separator}{right}"));
    }

    [Fact]
    public void Normalize_ReplacesTheParenthesesAroundATrailingYearWithSpaces()
    {
        var word = TestValues.LowercaseToken(8);
        var year = TestValues.NewReleaseYear();

        Assert.Equal($"{word} {year}", OpenCriticNameIndex.Normalize($"{word} ({year})"));
    }

    [Fact]
    public void Normalize_CollapsesRunsOfWhitespaceAndTrimsTheEnds()
    {
        var left = TestValues.LowercaseToken(6);
        var right = TestValues.LowercaseToken(7);

        Assert.Equal($"{left} {right}", OpenCriticNameIndex.Normalize($"  {left}   {right}  "));
    }

    [Fact]
    public void Normalize_OfAnEmptyTitleIsEmpty() =>
        Assert.Equal(string.Empty, OpenCriticNameIndex.Normalize(string.Empty));

    [Fact]
    public void Normalize_ConvertsARomanNumeralThatADashSeparatorHasJustExposed()
    {
        var word = TestValues.LowercaseToken(6);

        Assert.Equal($"{word} i 3", OpenCriticNameIndex.Normalize($"{word} I-III"));
    }

    [Fact]
    public void Normalize_ConvertsARomanNumeralAfterAnApostropheHasBeenRemoved()
    {
        var owner = TestValues.LowercaseToken(6);

        Assert.Equal($"{owner}s 3", OpenCriticNameIndex.Normalize($"{owner}'s III"));
    }

    [Fact]
    public void Normalize_FoldsASuperscriptDigitThatADashSeparatorHasJustExposed()
    {
        var left = TestValues.LowercaseToken(5);
        var right = TestValues.LowercaseToken(6);

        Assert.Equal($"{left} {right}2", OpenCriticNameIndex.Normalize($"{left}-{right}²"));
    }

    [Theory]
    [InlineData(": ")]
    [InlineData(" - ")]
    public void StripSubtitle_CutsAtASpacedColonOrDash(string separator)
    {
        var mainTitle = NewFillerWords();

        Assert.Equal(
            mainTitle,
            OpenCriticNameIndex.StripSubtitle($"{mainTitle}{separator}{NewFillerWords()}"));
    }

    [Fact]
    public void StripSubtitle_CutsAtTheFirstSeparatorRatherThanTheLast()
    {
        var mainTitle = NewFillerWords();

        var stripped = OpenCriticNameIndex.StripSubtitle(
            $"{mainTitle} - {NewFillerWords()} - {NewFillerWords()}");

        Assert.Equal(mainTitle, stripped);
    }

    [Fact]
    public void StripSubtitle_LeavesATitleCarryingNoSeparatorAlone()
    {
        var mainTitle = NewFillerWords();

        Assert.Equal(mainTitle, OpenCriticNameIndex.StripSubtitle(mainTitle));
    }

    [Fact]
    public void StripSubtitle_LeavesAColonThatIsNotFollowedByASpaceAlone()
    {
        var unspacedColonTitle = $"{TestValues.LowercaseToken(5)}:{TestValues.LowercaseToken(7)}";

        Assert.Equal(unspacedColonTitle, OpenCriticNameIndex.StripSubtitle(unspacedColonTitle));
    }

    [Fact]
    public void StripSubtitle_TrimsWhatRemainsWhenTheSeparatorIsTrailing()
    {
        var mainTitle = NewFillerWords();

        Assert.Equal(mainTitle, OpenCriticNameIndex.StripSubtitle($"{mainTitle} - "));
    }

    [Fact]
    public void FindMatch_Strategy1_ExactNormalizedMatch()
    {
        // Arrange
        var indexedGameId = NewOcGameId();
        var indexedTitle = NewGameTitle();
        var index = OpenCriticNameIndex.Build([Game(indexedGameId, indexedTitle)]);

        // Act
        var result = index.FindMatch(indexedTitle);

        // Assert
        Assert.Equal(indexedGameId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_Strategy2_SubtitleStrippedMatch()
    {
        // Arrange
        var indexedGameId = NewOcGameId();
        var indexedTitle = NewGameTitle();
        var index = OpenCriticNameIndex.Build([Game(indexedGameId, indexedTitle)]);

        // Act
        var result = index.FindMatch($"{indexedTitle}: {NewGameTitle()}");

        // Assert
        Assert.Equal(indexedGameId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_Strategy3_SpaceStrippedMatch()
    {
        // Arrange
        var indexedGameId = NewOcGameId();
        var firstWord = NewGameTitle();
        var secondWord = NewGameTitle();
        var index = OpenCriticNameIndex.Build([Game(indexedGameId, $"{firstWord} {secondWord}")]);

        // Act
        var result = index.FindMatch($"{firstWord}{secondWord}");

        // Assert
        Assert.Equal(indexedGameId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_Strategy5_OurTitleAppearsWordBoundedInsideACatalogName()
    {
        // Arrange
        var indexedGameId = NewOcGameId();
        var soughtTitle = NewGameTitle();
        var index = OpenCriticNameIndex.Build(
            [Game(indexedGameId, $"{NewGameTitle()}: {soughtTitle} - {NewGameTitle()}")]);

        // Act
        var result = index.FindMatch(soughtTitle);

        // Assert
        Assert.Equal(indexedGameId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_Strategy6_CatalogNameAppearsAtTheStartOfOurTitle()
    {
        // Arrange
        var indexedGameId = NewOcGameId();
        var catalogName = NewGameTitle();
        var index = OpenCriticNameIndex.Build([Game(indexedGameId, catalogName)]);

        // Act
        var result = index.FindMatch($"{catalogName} – {NewGameTitle()}");

        // Assert
        Assert.Equal(indexedGameId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_Strategy6_PrefersTheLongestMatchingCatalogName()
    {
        // Arrange
        var shorterMatchId = NewOcGameId();
        var longerMatchId = NewOcGameId();
        var shorterCatalogName = NewGameTitle();
        var longerCatalogName = $"{shorterCatalogName} {NewGameTitle()}";
        var index = OpenCriticNameIndex.Build(
            [Game(shorterMatchId, shorterCatalogName), Game(longerMatchId, longerCatalogName)]);

        // Act
        var result = index.FindMatch($"{longerCatalogName} {NewGameTitle()}");

        // Assert
        Assert.Equal(longerMatchId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_Strategy6_OnAnEqualLengthTieKeepsTheFirstIndexedCatalogName()
    {
        // Arrange
        var firstIndexedId = NewOcGameId();
        var secondIndexedId = NewOcGameId();
        var firstIndexedName = NewGameTitle();
        var secondIndexedName = $"{firstIndexedName}z";
        var index = OpenCriticNameIndex.Build(
            [Game(firstIndexedId, firstIndexedName), Game(secondIndexedId, secondIndexedName)]);

        // Act
        var result = index.FindMatch($"{firstIndexedName} {NewGameTitle()}");

        // Assert
        Assert.Equal(firstIndexedId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_WhenNothingMatches_ReturnsNull()
    {
        // Arrange
        var index = OpenCriticNameIndex.Build([Game(NewOcGameId(), NewGameTitle())]);

        // Act
        var match = index.FindMatch(NewGameTitle());

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void FindMatch_AmongDuplicateNames_PrefersTheHighestScoredCandidate()
    {
        // Arrange
        var lowerScoredId = NewOcGameId();
        var higherScoredId = NewOcGameId();
        var duplicatedName = NewGameTitle();
        var lowerScore = Random.Shared.Next(1, 50);
        var higherScore = Random.Shared.Next(51, 100);
        var index = OpenCriticNameIndex.Build(
            [Game(lowerScoredId, duplicatedName, lowerScore), Game(higherScoredId, duplicatedName, higherScore)]);

        // Act
        var result = index.FindMatch(duplicatedName);

        // Assert
        Assert.Equal(higherScoredId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_AmongDuplicateNamesTiedOnScore_KeepsTheFirstIndexed()
    {
        // Arrange
        var firstIndexedId = NewOcGameId();
        var secondIndexedId = NewOcGameId();
        var duplicatedName = NewGameTitle();
        var sharedScore = Random.Shared.Next(1, 101);
        var index = OpenCriticNameIndex.Build(
            [Game(firstIndexedId, duplicatedName, sharedScore), Game(secondIndexedId, duplicatedName, sharedScore)]);

        // Act
        var result = index.FindMatch(duplicatedName);

        // Assert
        Assert.Equal(firstIndexedId, result?.OcGameId);
    }

    [Fact]
    public void FindMatch_WhenNoCandidateHasAScore_FallsBackToTheFirstIndexed()
    {
        // Arrange
        var firstIndexedId = NewOcGameId();
        var secondIndexedId = NewOcGameId();
        var duplicatedName = NewGameTitle();
        var index = OpenCriticNameIndex.Build(
            [Game(firstIndexedId, duplicatedName, score: null), Game(secondIndexedId, duplicatedName, score: null)]);

        // Act
        var result = index.FindMatch(duplicatedName);

        // Assert
        Assert.Equal(firstIndexedId, result?.OcGameId);
    }

    [Fact]
    public void Build_IndexesTheYearSuffixStrippedNameToo()
    {
        // Arrange
        var indexedGameId = NewOcGameId();
        var titleWithoutYear = NewGameTitle();
        var releaseYear = Random.Shared.Next(1990, 2031);
        var index = OpenCriticNameIndex.Build([Game(indexedGameId, $"{titleWithoutYear} ({releaseYear})")]);

        // Act
        var result = index.FindMatch(titleWithoutYear);

        // Assert
        Assert.Equal(indexedGameId, result?.OcGameId);
    }

    [Fact]
    public void OpenCriticGame_TwoRecordsDifferingOnlyInTheirRawPayloadAreEqual()
    {
        // Arrange
        var sharedGameId = NewOcGameId();
        var sharedName = NewGameTitle();
        var sharedScore = Random.Shared.Next(1, 101);
        var sharedTier = $"Tier{Guid.NewGuid():N}";
        var sharedPercentRecommended = Random.Shared.Next(0, 101);
        var withoutRaw = new OpenCriticGame(
            sharedGameId, sharedName, sharedScore, sharedTier, sharedPercentRecommended);
        var withRaw = withoutRaw with { Raw = NewRawPayload() };

        // Assert
        Assert.Equal(withoutRaw, withRaw);
        Assert.Equal(withoutRaw.GetHashCode(), withRaw.GetHashCode());
    }

    private static int NewOcGameId() => Random.Shared.Next(1, 1_000_000);

    private static string NewGameTitle() => TestValues.NewGameTitle();

    private static string NewFillerWords() =>
        $"{TestValues.LowercaseToken(5)} {TestValues.LowercaseToken(7)}";

    private static string NewRawPayload() => $"{{\"id\":{Random.Shared.Next(1, 1_000_000)}}}";

    private static OpenCriticGame Game(int ocGameId, string name, double? score = null)
    {
        var tier = $"Tier{Guid.NewGuid():N}";
        var percentRecommended = Random.Shared.Next(0, 101);
        return new OpenCriticGame(ocGameId, name, score, tier, percentRecommended);
    }
}
