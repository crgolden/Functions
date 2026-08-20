namespace Functions.Tests.Unit;

using Curator.OpenCritic;

[Trait("Category", "Unit")]
public sealed class OpenCriticNameIndexTests
{
    [Theory]
    [InlineData("Bloodborne", "bloodborne")]
    [InlineData("Final Fantasy VII", "final fantasy 7")]
    [InlineData("Grand Theft Auto III", "grand theft auto 3")]
    [InlineData("Resident Evil VIII", "resident evil 8")]
    [InlineData("Final Fantasy IX", "final fantasy 9")]
    [InlineData("Final Fantasy IV", "final fantasy 4")]
    [InlineData("Final Fantasy VI", "final fantasy 6")]
    [InlineData("Final Fantasy II", "final fantasy 2")]
    [InlineData("Devil May Cry V", "devil may cry v")]
    [InlineData("Metal Gear Solid V: The Phantom Pain", "metal gear solid v the phantom pain")]
    [InlineData("Baldur's Gate III", "baldurs gate 3")]
    public void Normalize_ConvertsOnlyTheRomanNumeralsCuratorActuallyMaps(string title, string expected) =>
        Assert.Equal(expected, OpenCriticNameIndex.Normalize(title));

    [Theory]
    [InlineData("Marvel's Spider-Man 2", "marvels spider man 2")]
    [InlineData("Ghost of Tsushima - Director's Cut", "ghost of tsushima directors cut")]
    [InlineData("Uncharted 4: A Thief's End", "uncharted 4 a thiefs end")]
    [InlineData("Marvel’s Spider-Man 2", "marvels spider man 2")]
    [InlineData("Assassin’s Creed", "assassins creed")]
    [InlineData("Demonʼs Souls", "demons souls")]
    public void Normalize_RemovesTypographicApostrophesTheSameAsAsciiOnes(string title, string expected) =>
        Assert.Equal(expected, OpenCriticNameIndex.Normalize(title));

    [Theory]
    [InlineData("Marvel's Spider-Man™ 2", "marvels spider mantm 2")]
    [InlineData("LittleBigPlanet™ 3", "littlebigplanettm 3")]
    [InlineData("Demon's Souls™", "demons soulstm")]
    public void Normalize_LeavesTmGluedToTheWordBecauseCompatibilityDecompositionRunsFirst(
        string title, string expected) =>
        Assert.Equal(expected, OpenCriticNameIndex.Normalize(title));

    [Theory]
    [InlineData("Assassin's Creed® Valhalla", "assassins creed valhalla")]
    [InlineData("Star Wars Jedi: Fallen Order©", "star wars jedi fallen order")]
    [InlineData("WipEout® Omega Collection", "wipeout omega collection")]
    [InlineData("Journey (tm)", "journey")]
    [InlineData("Journey (r)", "journey")]
    [InlineData("Journey (c)", "journey")]
    public void Normalize_StripsRegisteredAndCopyrightSignsAndTheirParenthesisedForms(string title, string expected) =>
        Assert.Equal(expected, OpenCriticNameIndex.Normalize(title));

    [Theory]
    [InlineData("Pokémon Café Mix", "pokemon cafe mix")]
    [InlineData("Élite Dangerous", "elite dangerous")]
    [InlineData("№ 9", "no 9")]
    [InlineData("Half-Life²", "half life2")]
    [InlineData("Tekken⁴", "tekken4")]
    public void Normalize_FoldsAccentsAndCompatibilityCharacters(string title, string expected) =>
        Assert.Equal(expected, OpenCriticNameIndex.Normalize(title));

    [Theory]
    [InlineData("Grand Theft Auto III – The Definitive Edition", "grand theft auto 3 the definitive edition")]
    [InlineData("Nioh 2 – The Complete Edition", "nioh 2 the complete edition")]
    [InlineData("Tomb Raider I-III Remastered", "tomb raider i 3 remastered")]
    [InlineData("R-Type Final 2", "r type final 2")]
    [InlineData("NieR:Automata", "nier automata")]
    [InlineData("Ratchet & Clank: Rift Apart", "ratchet clank rift apart")]
    [InlineData("F.E.A.R.", "f e a r")]
    [InlineData("Dead Space (2023)", "dead space 2023")]
    [InlineData("  spaced   out  title  ", "spaced out title")]
    [InlineData("", "")]
    public void Normalize_ReplacesSeparatorsAndCollapsesWhitespace(string title, string expected) =>
        Assert.Equal(expected, OpenCriticNameIndex.Normalize(title));

    [Theory]
    [InlineData("Sekiro: Shadows Die Twice", "Sekiro")]
    [InlineData("Ghost of Tsushima - Director's Cut", "Ghost of Tsushima")]
    [InlineData("Returnal", "Returnal")]
    [InlineData("The Elder Scrolls V: Skyrim - Special Edition", "The Elder Scrolls V")]
    [InlineData("A - B - C", "A")]
    [InlineData("A: B: C", "A")]
    [InlineData("NieR:Automata", "NieR:Automata")]
    [InlineData("Trailing - ", "Trailing")]
    public void StripSubtitle_CutsAtTheFirstSpacedColonOrDashOnly(string title, string expected) =>
        Assert.Equal(expected, OpenCriticNameIndex.StripSubtitle(title));

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

    private static string NewGameTitle() => $"Game{Guid.NewGuid():N}";

    private static string NewRawPayload() => $"{{\"id\":{Random.Shared.Next(1, 1_000_000)}}}";

    private static OpenCriticGame Game(int ocGameId, string name, double? score = null)
    {
        var tier = $"Tier{Guid.NewGuid():N}";
        var percentRecommended = Random.Shared.Next(0, 101);
        return new OpenCriticGame(ocGameId, name, score, tier, percentRecommended);
    }
}
