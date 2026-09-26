namespace Functions.Tests.Unit;

using Functions.Curator.OpenCritic;
using static Functions.Tests.Unit.OpenCriticNameIndexFixtureConstants;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class OpenCriticNameIndexTests
{
    public static TheoryData<int> CuratorMappedRomanNumeralIndexes() =>
        [.. Enumerable.Range(0, OpenCriticNameIndex.RomanNumeralsToArabic.Length)];

    public static TheoryData<string> RomanNumeralsCuratorLeavesAlone() => [RomanOne, RomanFive, RomanTen];

    public static TheoryData<string> ParenthesisedMarkForms() =>
        [ParenthesisedTrademark, ParenthesisedRegistered, ParenthesisedCopyright];

    public static TheoryData<string> SubtitleSeparators() => [.. OpenCriticNameIndex.SubtitleSeparators];

    [Fact]
    public void RomanNumeralsToArabic_MapsExactlyTheNumeralsCuratorsMatcherMaps()
    {
        // Act
        var mapped = OpenCriticNameIndex.RomanNumeralsToArabic;

        // Assert
        Assert.Equal(
            [
                (CuratorRomanEight, CuratorArabicEight),
                (CuratorRomanSeven, CuratorArabicSeven),
                (CuratorRomanSix, CuratorArabicSix),
                (CuratorRomanNine, CuratorArabicNine),
                (CuratorRomanFour, CuratorArabicFour),
                (CuratorRomanThree, CuratorArabicThree),
                (CuratorRomanTwo, CuratorArabicTwo),
            ],
            mapped);
    }

    [Fact]
    public void SubtitleSeparators_AreTheSpacedColonAndTheSpacedDash()
    {
        // Act
        var separators = OpenCriticNameIndex.SubtitleSeparators;

        // Assert
        Assert.Equal([": ", " - "], separators);
    }

    [Theory]
    [MemberData(nameof(CuratorMappedRomanNumeralIndexes))]
    public void Normalize_ConvertsTheRomanNumeralsCuratorMaps(int mappedNumeralIndex)
    {
        // Arrange
        var (numeral, arabic) = OpenCriticNameIndex.RomanNumeralsToArabic[mappedNumeralIndex];
        var precedingWords = NewFillerWords();

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{precedingWords} {numeral.ToUpperInvariant()}");

        // Assert
        Assert.Equal($"{precedingWords} {arabic}", normalized);
    }

    [Theory]
    [MemberData(nameof(RomanNumeralsCuratorLeavesAlone))]
    public void Normalize_OnlyLowercasesARomanNumeralCuratorDoesNotMap(string numeral)
    {
        // Arrange
        var precedingWords = NewFillerWords();

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{precedingWords} {numeral}");

        // Assert
        Assert.Equal($"{precedingWords} {numeral.ToLowerInvariant()}", normalized);
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('’')]
    [InlineData('‘')]
    [InlineData('ʼ')]
    [InlineData('`')]
    public void Normalize_RemovesTypographicApostrophesTheSameAsAsciiOnes(char apostrophe)
    {
        // Arrange
        var owner = Generated.LowercaseToken(6);
        var possessiveLetter = Generated.LowercaseToken(1);
        var possession = Generated.LowercaseToken(7);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{owner}{apostrophe}{possessiveLetter} {possession}");

        // Assert
        Assert.Equal($"{owner}{possessiveLetter} {possession}", normalized);
    }

    [Fact]
    public void Normalize_LeavesTmGluedToTheWordBecauseCompatibilityDecompositionRunsFirst()
    {
        // Arrange
        var word = Generated.LowercaseToken(8);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{word}{TrademarkSign}");

        // Assert
        Assert.Equal($"{word}{TrademarkSignCompatibilityForm}", normalized);
    }

    [Theory]
    [InlineData('®')]
    [InlineData('©')]
    public void Normalize_StripsRegisteredAndCopyrightSigns(char sign)
    {
        // Arrange
        var word = Generated.LowercaseToken(8);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{word}{sign}");

        // Assert
        Assert.Equal(word, normalized);
    }

    [Theory]
    [MemberData(nameof(ParenthesisedMarkForms))]
    public void Normalize_StripsTheParenthesisedMarkForms(string mark)
    {
        // Arrange
        var word = Generated.LowercaseToken(8);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{word} {mark}");

        // Assert
        Assert.Equal(word, normalized);
    }

    [Theory]
    [InlineData('é', 'e')]
    [InlineData('è', 'e')]
    [InlineData('á', 'a')]
    [InlineData('ü', 'u')]
    public void Normalize_FoldsAnAccentedLetterOntoItsBaseLetter(char accented, char folded)
    {
        // Arrange
        var before = Generated.LowercaseToken(4);
        var after = Generated.LowercaseToken(5);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{before}{accented}{after}");

        // Assert
        Assert.Equal($"{before}{folded}{after}", normalized);
    }

    [Theory]
    [InlineData('²', '2')]
    [InlineData('⁴', '4')]
    public void Normalize_FoldsASuperscriptDigitOntoItsAsciiForm(char superscript, char digit)
    {
        // Arrange
        var word = Generated.LowercaseToken(8);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{word}{superscript}");

        // Assert
        Assert.Equal($"{word}{digit}", normalized);
    }

    [Fact]
    public void Normalize_FoldsTheNumeroSignOntoNo()
    {
        // Arrange
        var word = Generated.LowercaseToken(8);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{NumeroSign} {word}");

        // Assert
        Assert.Equal($"{NumeroSignCompatibilityForm} {word}", normalized);
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
        // Arrange
        var left = Generated.LowercaseToken(6);
        var right = Generated.LowercaseToken(7);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{left}{separator}{right}");

        // Assert
        Assert.Equal($"{left} {right}", normalized);
    }

    [Fact]
    public void Normalize_ReplacesTheParenthesesAroundATrailingYearWithSpaces()
    {
        // Arrange
        var word = Generated.LowercaseToken(8);
        var year = Generated.NewReleaseYear();

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{word} ({year})");

        // Assert
        Assert.Equal($"{word} {year}", normalized);
    }

    [Fact]
    public void Normalize_CollapsesRunsOfWhitespaceAndTrimsTheEnds()
    {
        // Arrange
        var left = Generated.LowercaseToken(6);
        var right = Generated.LowercaseToken(7);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"  {left}   {right}  ");

        // Assert
        Assert.Equal($"{left} {right}", normalized);
    }

    [Fact]
    public void Normalize_OfAnEmptyTitleIsEmpty() =>
        Assert.Equal(string.Empty, OpenCriticNameIndex.Normalize(string.Empty));

    [Fact]
    public void Normalize_ConvertsARomanNumeralThatADashSeparatorHasJustExposed()
    {
        // Arrange
        var word = Generated.LowercaseToken(6);
        var mappedNumeralIndex = Random.Shared.Next(OpenCriticNameIndex.RomanNumeralsToArabic.Length);
        var (numeral, arabic) = OpenCriticNameIndex.RomanNumeralsToArabic[mappedNumeralIndex];

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{word} {RomanOne}-{numeral.ToUpperInvariant()}");

        // Assert
        Assert.Equal($"{word} {RomanOne.ToLowerInvariant()} {arabic}", normalized);
    }

    [Fact]
    public void Normalize_ConvertsARomanNumeralAfterAnApostropheHasBeenRemoved()
    {
        // Arrange
        var owner = Generated.LowercaseToken(6);
        var possessiveLetter = Generated.LowercaseToken(1);
        var mappedNumeralIndex = Random.Shared.Next(OpenCriticNameIndex.RomanNumeralsToArabic.Length);
        var (numeral, arabic) = OpenCriticNameIndex.RomanNumeralsToArabic[mappedNumeralIndex];

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{owner}{AsciiApostrophe}{possessiveLetter} {numeral.ToUpperInvariant()}");

        // Assert
        Assert.Equal($"{owner}{possessiveLetter} {arabic}", normalized);
    }

    [Fact]
    public void Normalize_FoldsASuperscriptDigitThatADashSeparatorHasJustExposed()
    {
        // Arrange
        var left = Generated.LowercaseToken(5);
        var right = Generated.LowercaseToken(6);

        // Act
        var normalized = OpenCriticNameIndex.Normalize($"{left}-{right}{SuperscriptTwo}");

        // Assert
        Assert.Equal($"{left} {right}{SuperscriptTwoCompatibilityForm}", normalized);
    }

    [Theory]
    [MemberData(nameof(SubtitleSeparators))]
    public void StripSubtitle_CutsAtASpacedColonOrDash(string separator)
    {
        // Arrange
        var mainTitle = NewFillerWords();

        // Act
        var stripped = OpenCriticNameIndex.StripSubtitle($"{mainTitle}{separator}{NewFillerWords()}");

        // Assert
        Assert.Equal(mainTitle, stripped);
    }

    [Fact]
    public void StripSubtitle_CutsAtTheFirstSeparatorRatherThanTheLast()
    {
        // Arrange
        var mainTitle = NewFillerWords();

        // Act
        var stripped = OpenCriticNameIndex.StripSubtitle(
            $"{mainTitle} - {NewFillerWords()} - {NewFillerWords()}");

        // Assert
        Assert.Equal(mainTitle, stripped);
    }

    [Fact]
    public void StripSubtitle_LeavesATitleCarryingNoSeparatorAlone()
    {
        // Arrange
        var mainTitle = NewFillerWords();

        // Act
        var stripped = OpenCriticNameIndex.StripSubtitle(mainTitle);

        // Assert
        Assert.Equal(mainTitle, stripped);
    }

    [Fact]
    public void StripSubtitle_LeavesAColonThatIsNotFollowedByASpaceAlone()
    {
        // Arrange
        var unspacedColonTitle = $"{Generated.LowercaseToken(5)}:{Generated.LowercaseToken(7)}";

        // Act
        var stripped = OpenCriticNameIndex.StripSubtitle(unspacedColonTitle);

        // Assert
        Assert.Equal(unspacedColonTitle, stripped);
    }

    [Fact]
    public void StripSubtitle_TrimsWhatRemainsWhenTheSeparatorIsTrailing()
    {
        // Arrange
        var mainTitle = NewFillerWords();

        // Act
        var stripped = OpenCriticNameIndex.StripSubtitle($"{mainTitle} - ");

        // Assert
        Assert.Equal(mainTitle, stripped);
    }

    [Fact]
    public void FindMatch_Strategy1_ExactNormalizedMatch()
    {
        // Arrange
        var indexedGameId = NewOpenCriticGameId();
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
        var indexedGameId = NewOpenCriticGameId();
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
        var indexedGameId = NewOpenCriticGameId();
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
        var indexedGameId = NewOpenCriticGameId();
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
        var indexedGameId = NewOpenCriticGameId();
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
        var shorterMatchId = NewOpenCriticGameId();
        var longerMatchId = NewOpenCriticGameId();
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
        var firstIndexedId = NewOpenCriticGameId();
        var secondIndexedId = NewOpenCriticGameId();
        var firstIndexedName = NewGameTitle();
        var secondIndexedName = WithATrailingLetter(firstIndexedName);
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
        var index = OpenCriticNameIndex.Build([Game(NewOpenCriticGameId(), NewGameTitle())]);

        // Act
        var match = index.FindMatch(NewGameTitle());

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void FindMatch_AmongDuplicateNames_PrefersTheHighestScoredCandidate()
    {
        // Arrange
        var lowerScoredId = NewOpenCriticGameId();
        var higherScoredId = NewOpenCriticGameId();
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
        var firstIndexedId = NewOpenCriticGameId();
        var secondIndexedId = NewOpenCriticGameId();
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
        var firstIndexedId = NewOpenCriticGameId();
        var secondIndexedId = NewOpenCriticGameId();
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
        var indexedGameId = NewOpenCriticGameId();
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
        var sharedGameId = NewOpenCriticGameId();
        var sharedName = NewGameTitle();
        var sharedScore = Random.Shared.Next(1, 101);
        var sharedTier = $"Tier{Guid.NewGuid():N}";
        var sharedPercentRecommended = Random.Shared.Next(0, 101);
        var withoutRaw = new OpenCriticGame(
            sharedGameId, sharedName, sharedScore, sharedTier, sharedPercentRecommended);

        // Act
        var withRaw = withoutRaw with { Raw = NewOpenCriticRawPayload() };

        // Assert
        Assert.Equal(withoutRaw, withRaw);
        Assert.Equal(withoutRaw.GetHashCode(), withRaw.GetHashCode());
    }

    private static string NewFillerWords() =>
        $"{Generated.LowercaseToken(5)} {Generated.LowercaseToken(7)}";

    private static OpenCriticGame Game(int ocGameId, string name, double? score = null)
    {
        var tier = $"Tier{Guid.NewGuid():N}";
        var percentRecommended = Random.Shared.Next(0, 101);
        return new OpenCriticGame(ocGameId, name, score, tier, percentRecommended);
    }
}
