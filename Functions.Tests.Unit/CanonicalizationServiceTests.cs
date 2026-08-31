namespace Functions.Tests.Unit;

using Curator.Catalog;
using Curator.Library;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class CanonicalizationServiceTests
{
    private const string Ps5PackageType = "PSGD";
    private const string Ps4PackageType = "PS4GD";
    private const string AddOnPackageType = "PS4AC";
    private const string AddOnLicencePackageType = "PS4AL";
    private const string SubscriptionTitleId = "SUBC00001_00";
    private const string Ps3TitleId = "BLUS30233_00";
    private const string Ps4TitleId = "CUSA00011_00";
    private const string AccentedLetter = "é";
    private const string BaseLetterOfTheAccentedLetter = "e";

    private static readonly IReadOnlyDictionary<string, int> NoEditionRanks = new Dictionary<string, int>();
    private static readonly IReadOnlyDictionary<string, string> NoNameOverrides = new Dictionary<string, string>();

    [Theory]
    [InlineData("™")]
    [InlineData("®")]
    [InlineData("©")]
    [InlineData("TM")]
    public void NormalizeName_StripsATrailingTrademarkMarker(string marker)
    {
        // Arrange
        var title = TestValues.NewGameTitle();

        // Act
        var normalized = CanonicalizationService.NormalizeName($"{title}{marker}");

        // Assert
        Assert.Equal(title, normalized);
    }

    [Fact]
    public void NormalizeName_CollapsesARunOfSpacesToOne()
    {
        // Arrange
        var firstWord = TestValues.LowercaseToken(6);
        var secondWord = TestValues.LowercaseToken(8);

        // Act
        var normalized = CanonicalizationService.NormalizeName($"{firstWord}   {secondWord}");

        // Assert
        Assert.Equal($"{firstWord} {secondWord}", normalized);
    }

    [Fact]
    public void NormalizeName_TrimsSurroundingWhitespace()
    {
        // Arrange
        var title = TestValues.NewGameTitle();

        // Act
        var normalized = CanonicalizationService.NormalizeName($"  {title}  ");

        // Assert
        Assert.Equal(title, normalized);
    }

    [Fact]
    public void NormalizeName_RemovesTheEmptyParenthesesLeftBehindByStrippingTrademarkLetters()
    {
        // Arrange
        var title = TestValues.NewGameTitle();

        // Act
        var normalized = CanonicalizationService.NormalizeName($"{title} (TM)");

        // Assert
        Assert.Equal(title, normalized);
    }

    [Fact]
    public void NormalizeName_StripsDiacriticsWithoutDroppingTheBaseLetter()
    {
        // Arrange
        var rest = TestValues.LowercaseToken(8);

        // Act
        var normalized = CanonicalizationService.NormalizeName($"{AccentedLetter}{rest}");

        // Assert
        Assert.Equal($"{BaseLetterOfTheAccentedLetter}{rest}", normalized);
    }

    [Fact]
    public void EditionRank_ReturnsTheRankOfTheLowestRankedMatchingKeyword()
    {
        // Arrange
        var higherRankedKeyword = NewEditionKeyword();
        var lowerRankedKeyword = NewEditionKeyword();
        var lowestRank = NewEditionRank();
        var ranks = new Dictionary<string, int>
        {
            [higherRankedKeyword] = lowestRank + 1,
            [lowerRankedKeyword] = lowestRank,
        };

        // Act
        var rank = CanonicalizationService.EditionRank(
            $"{NewGameTitle()} {lowerRankedKeyword.ToUpperInvariant()} {higherRankedKeyword.ToUpperInvariant()} Edition",
            ranks);

        // Assert
        Assert.Equal(lowestRank, rank);
    }

    [Fact]
    public void EditionRank_ReturnsTheUnrankedValue_WhenNoKeywordMatches()
    {
        // Arrange
        var ranks = new Dictionary<string, int> { [NewEditionKeyword()] = NewEditionRank() };

        // Act
        var rank = CanonicalizationService.EditionRank(NewGameTitle(), ranks);

        // Assert
        Assert.Equal(CanonicalizationService.UnrankedEdition, rank);
    }

    [Fact]
    public void Canonicalize_ProducesOneGamePerConceptId()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps5PackageType),
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps4PackageType),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        var game = Assert.Single(games);
        Assert.Equal(title, game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_PrefersThePs5NativeEditionAsTheWinner()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewGameTitle();
        var expectedWinningEntitlementId = NewEntitlementId();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps4PackageType),
            Snapshot(expectedWinningEntitlementId, conceptId: conceptId, titleMetaName: title, packageType: Ps5PackageType),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.True(game.NativePs5);
        Assert.Equal(expectedWinningEntitlementId, game.WinningEntitlementId);
    }

    [Fact]
    public void Canonicalize_ReportsPs4Eligibility_WhenAnyEntryIsAPs4Edition()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps5PackageType),
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps4PackageType),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.True(game.Ps4Eligible);
    }

    [Fact]
    public void Canonicalize_PrefersAnActiveEntitlementOverALapsedOne()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewGameTitle();
        var expectedWinningEntitlementId = NewEntitlementId();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps5PackageType, active: false),
            Snapshot(expectedWinningEntitlementId, conceptId: conceptId, titleMetaName: title, packageType: Ps4PackageType, active: true),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal(expectedWinningEntitlementId, game.WinningEntitlementId);
    }

    [Fact]
    public void Canonicalize_ReportsAGameAsActive_WhenAnyOfItsEntitlementsStillIs()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps5PackageType, active: false),
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps4PackageType, active: true),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.True(game.Active);
    }

    [Fact]
    public void Canonicalize_DropsAConceptTheOperatorHasGloballyExcluded()
    {
        // Arrange
        var excludedConceptId = NewConceptId();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: excludedConceptId, titleMetaName: NewGameTitle(), packageType: Ps5PackageType),
        };
        var excluded = new HashSet<string>(StringComparer.Ordinal) { excludedConceptId };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides, excluded);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public void Canonicalize_DropsANonTitleEntitlementSuchAsASubscription()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleId: SubscriptionTitleId, titleMetaName: NewGameTitle(), packageType: Ps5PackageType),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public void Canonicalize_DropsAnEntitlementPsnItselfSaysIsNotAGame()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleMetaName: NewGameTitle(), packageType: Ps5PackageType, isGame: false),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public void Canonicalize_DropsAnAddOnByItsPackageType()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleMetaName: NewGameTitle(), packageType: AddOnPackageType),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public void Canonicalize_KeepsAnUnclassifiedEntitlement_WhenNoSiblingOfThatTitleWasClassifiedNonGame()
    {
        // Arrange
        var title = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleId: Ps3TitleId, titleMetaName: title),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal(title, game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_DropsAnUnclassifiedEntitlement_WhenEveryClassifiedSiblingOfItsTitleIsNonGame()
    {
        // Arrange
        var sharedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleId: Ps4TitleId, titleMetaName: sharedTitle, packageType: AddOnLicencePackageType),
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleId: Ps4TitleId, titleMetaName: sharedTitle),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public void Canonicalize_AppliesAnExclusionRuleToTheGameMetaName()
    {
        // Arrange
        var excludedName = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), gameMetaName: excludedName, titleMetaName: excludedName, packageType: Ps5PackageType),
        };
        var rules = new[] { new ExclusionRule(Guid.NewGuid(), ExclusionRules.MediaApp, excludedName) };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, rules, [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public void Canonicalize_PrefersAnOperatorNameOverrideForTheDisplayTitle()
    {
        // Arrange
        var conceptId = NewConceptId();
        var expectedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: NewGameTitle(), packageType: Ps5PackageType),
        };
        var overrides = new Dictionary<string, string> { [conceptId] = expectedTitle };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, overrides));

        // Assert
        Assert.Equal(expectedTitle, game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_FallsThroughAnEmptyOverrideToThePsnSuppliedName()
    {
        // Arrange
        var conceptId = NewConceptId();
        var psnSuppliedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: psnSuppliedTitle, packageType: Ps5PackageType),
        };
        var overrides = new Dictionary<string, string> { [conceptId] = string.Empty };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, overrides));

        // Assert
        Assert.Equal(psnSuppliedTitle, game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_DropsAnEntitlementWhoseNameNormalisesToNothing()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleMetaName: "™", packageType: Ps5PackageType),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public void Canonicalize_GroupsByNormalisedName_WhenPsnSuppliesNoConceptId()
    {
        // Arrange
        var sharedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), titleMetaName: sharedTitle, packageType: Ps5PackageType),
            Snapshot(NewEntitlementId(), titleMetaName: sharedTitle, packageType: Ps4PackageType),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Single(games);
    }

    [Fact]
    public void Canonicalize_UnionsThePlatformsAcrossEveryMergedEntitlement()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps5PackageType)
                with { PlatformIds = ["ps5"] },
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps4PackageType)
                with { PlatformIds = ["ps4"] },
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal(["PS4", "PS5"], game.Platforms);
    }

    [Fact]
    public void Canonicalize_FallsBackToTheTitleIdPrefix_WhenPsnPublishesNoPlatformAttribute()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleId: Ps3TitleId, titleMetaName: NewGameTitle(), packageType: Ps5PackageType),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal(["PS3"], game.Platforms);
    }

    [Fact]
    public void Canonicalize_SortsTheResultByTitleCaseInsensitively()
    {
        // Arrange
        var lowerCaseFirstTitle = $"a{Guid.NewGuid():N}";
        var upperCaseSecondTitle = $"B{Guid.NewGuid():N}";
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleMetaName: upperCaseSecondTitle, packageType: Ps5PackageType),
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleMetaName: lowerCaseFirstTitle, packageType: Ps5PackageType),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Equal([lowerCaseFirstTitle, upperCaseSecondTitle], games.Select(game => game.CanonicalTitle));
    }

    [Fact]
    public void Canonicalize_CollectsEveryConceptIdTheMergedEntriesCarried()
    {
        // Arrange
        var firstConceptId = $"a{Guid.NewGuid():N}";
        var secondConceptId = $"b{Guid.NewGuid():N}";
        var sharedProductId = NewProductId();
        var sharedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: secondConceptId, productId: sharedProductId, titleMetaName: sharedTitle, packageType: Ps5PackageType),
            Snapshot(NewEntitlementId(), conceptId: firstConceptId, productId: sharedProductId, titleMetaName: sharedTitle, packageType: Ps5PackageType),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal([firstConceptId, secondConceptId], game.ConceptIds);
    }

    private static string NewEntitlementId() => TestValues.NewEntitlementId();

    private static string NewConceptId() => TestValues.NewConceptId();

    private static string NewProductId() => TestValues.NewProductId();

    private static string NewGameTitle() => TestValues.NewGameTitle();

    private static string NewEditionKeyword() => $"edition{Guid.NewGuid():N}";

    private static int NewEditionRank() => Random.Shared.Next(2, 100);

    private static EntitlementSnapshot Snapshot(
        string entitlementId,
        string? conceptId = null,
        string? productId = null,
        string? titleId = null,
        string? gameMetaName = null,
        string? titleMetaName = null,
        string? packageType = null,
        bool? active = true,
        bool? isGame = null) =>
        new(entitlementId)
        {
            ConceptId = conceptId,
            ProductId = productId,
            TitleId = titleId,
            GameMetaName = gameMetaName,
            TitleMetaName = titleMetaName,
            PackageType = packageType,
            Active = active,
            IsGame = isGame,
        };
}
