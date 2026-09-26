namespace Functions.Tests.Unit;

using Functions.Curator.Catalog;
using Functions.Curator.Library;
using Functions.Curator.Psn;
using static Functions.Tests.Unit.CanonicalizationServiceFixtureConstants;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class CanonicalizationServiceTests
{
    private static readonly IReadOnlyDictionary<string, int> NoEditionRanks = new Dictionary<string, int>();
    private static readonly IReadOnlyDictionary<NameOverrideKey, string> NoNameOverrides = new Dictionary<NameOverrideKey, string>();

    public static TheoryData<string> TrailingTrademarkMarkers() => [TrademarkSign, RegisteredSign, CopyrightSign, TrademarkLetters];

    [Theory]
    [MemberData(nameof(TrailingTrademarkMarkers))]
    public void NormalizeName_StripsATrailingTrademarkMarker(string marker)
    {
        // Arrange
        var title = Generated.NewGameTitle();

        // Act
        var normalized = CanonicalizationService.NormalizeName($"{title}{marker}");

        // Assert
        Assert.Equal(title, normalized);
    }

    [Fact]
    public void NormalizeName_CollapsesARunOfSpacesToOne()
    {
        // Arrange
        var firstWord = Generated.LowercaseToken(6);
        var secondWord = Generated.LowercaseToken(8);

        // Act
        var normalized = CanonicalizationService.NormalizeName($"{firstWord}   {secondWord}");

        // Assert
        Assert.Equal($"{firstWord} {secondWord}", normalized);
    }

    [Fact]
    public void NormalizeName_TrimsSurroundingWhitespace()
    {
        // Arrange
        var title = Generated.NewGameTitle();

        // Act
        var normalized = CanonicalizationService.NormalizeName($"  {title}  ");

        // Assert
        Assert.Equal(title, normalized);
    }

    [Fact]
    public void NormalizeName_RemovesTheEmptyParenthesesLeftBehindByStrippingTrademarkLetters()
    {
        // Arrange
        var title = Generated.NewGameTitle();

        // Act
        var normalized = CanonicalizationService.NormalizeName($"{title} ({TrademarkLetters})");

        // Assert
        Assert.Equal(title, normalized);
    }

    [Fact]
    public void NormalizeName_StripsDiacriticsWithoutDroppingTheBaseLetter()
    {
        // Arrange
        var rest = Generated.LowercaseToken(8);

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
            $"{NewGameTitle()} {lowerRankedKeyword.ToUpperInvariant()} {higherRankedKeyword.ToUpperInvariant()}",
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
            snapshots, [], NoEditionRanks, NoNameOverrides);

        // Assert
        var game = Assert.Single(games);
        Assert.Equal(title, game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_KeepsTwoDifferentlyNamedProductsUnderOneConceptAsTwoGames()
    {
        // Arrange
        var conceptId = NewConceptId();
        var gameTitle = NewGameTitle();
        var soundtrackTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleId: Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), titleMetaName: soundtrackTitle, packageType: Ps4PackageType),
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleId: Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), titleMetaName: gameTitle, packageType: Ps4PackageType),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Equal(
            new[] { gameTitle, soundtrackTitle }.Order(StringComparer.Ordinal),
            games.Select(game => game.CanonicalTitle).Order(StringComparer.Ordinal));
        Assert.All(games, game => Assert.Equal([conceptId], game.ConceptIds));
    }

    [Fact]
    public void Canonicalize_KeepsThePlatformEditionsOfOneTitleUnderOneConceptAsOneGame()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleId: Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), titleMetaName: title, packageType: Ps4PackageType)
                with { PlatformIds = [TitlePlatform.Ps4PlatformId] },
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleId: Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix), titleMetaName: title, packageType: Ps5PackageType)
                with { PlatformIds = [TitlePlatform.Ps5PlatformId] },
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal([TitlePlatform.Ps4, TitlePlatform.Ps5], game.Platforms);
    }

    [Fact]
    public void Canonicalize_KeepsTheEditionsOfOneTitleTogether_AndLetsTheLowestRankNameTheGame()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewGameTitle();
        var baseKeyword = NewEditionKeyword();
        var upgradedKeyword = NewEditionKeyword();
        var baseRank = NewEditionRank();
        var ranks = new Dictionary<string, int> { [baseKeyword] = baseRank, [upgradedKeyword] = baseRank + 1 };
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleId: Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), titleMetaName: $"{title} {upgradedKeyword}", packageType: Ps4PackageType),
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleId: Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), titleMetaName: $"{title} {baseKeyword}", packageType: Ps4PackageType),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], ranks, NoNameOverrides));

        // Assert
        Assert.Equal($"{title} {baseKeyword}", game.CanonicalTitle);
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
            snapshots, [], NoEditionRanks, NoNameOverrides));

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
            snapshots, [], NoEditionRanks, NoNameOverrides));

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
            snapshots, [], NoEditionRanks, NoNameOverrides));

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
            snapshots, [], NoEditionRanks, NoNameOverrides));

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
            snapshots, [], NoEditionRanks, NoNameOverrides, excluded);

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
            snapshots, [], NoEditionRanks, NoNameOverrides);

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
            snapshots, [], NoEditionRanks, NoNameOverrides);

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
            snapshots, [], NoEditionRanks, NoNameOverrides);

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
            snapshots, [], NoEditionRanks, NoNameOverrides));

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
            snapshots, [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Empty(games);
    }

    [Fact]
    public void Canonicalize_KeepsAMediaAppAsACandidateOfItsOwnKind_RatherThanDroppingIt()
    {
        // Arrange
        var title = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleMetaName: title, packageType: ContentKinds.MediaAppPackageType, isGame: false),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal(title, game.CanonicalTitle);
        Assert.Equal(ContentKind.MediaApp, game.ContentKind);
    }

    [Fact]
    public void Canonicalize_ClassifiesAGameDownloadAsAGame()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleMetaName: NewGameTitle(), packageType: Ps5PackageType),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal(ContentKind.Game, game.ContentKind);
    }

    [Fact]
    public void Canonicalize_LeavesTheContentKindUnknown_WhenNoEntryCarriesAPackageType()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleId: Ps3TitleId, titleMetaName: NewGameTitle()),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Null(game.ContentKind);
    }

    [Fact]
    public void Canonicalize_KeepsAnUnclassifiedSibling_WhenTheOnlyClassifiedSiblingIsAMediaApp()
    {
        // Arrange
        var sharedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleId: Ps4TitleId, titleMetaName: sharedTitle, packageType: ContentKinds.MediaAppPackageType),
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleId: Ps4TitleId, titleMetaName: sharedTitle),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.NotEmpty(games);
    }

    [Fact]
    public void Canonicalize_PrefersAnOperatorNameOverrideForTheDisplayTitle()
    {
        // Arrange
        var conceptId = NewConceptId();
        var productId = NewProductId();
        var expectedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, productId: productId, titleMetaName: NewGameTitle(), packageType: Ps5PackageType),
        };
        var overrides = new Dictionary<NameOverrideKey, string> { [new NameOverrideKey(conceptId, productId)] = expectedTitle };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, overrides));

        // Assert
        Assert.Equal(expectedTitle, game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_FallsThroughAnEmptyOverrideToThePsnSuppliedName()
    {
        // Arrange
        var conceptId = NewConceptId();
        var productId = NewProductId();
        var psnSuppliedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, productId: productId, titleMetaName: psnSuppliedTitle, packageType: Ps5PackageType),
        };
        var overrides = new Dictionary<NameOverrideKey, string> { [new NameOverrideKey(conceptId, productId)] = string.Empty };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, overrides));

        // Assert
        Assert.Equal(psnSuppliedTitle, game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_DropsAnEntitlementWhoseNameNormalisesToNothing()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: NewConceptId(), titleMetaName: TrademarkSign, packageType: Ps5PackageType),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, NoNameOverrides);

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
            snapshots, [], NoEditionRanks, NoNameOverrides);

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
                with { PlatformIds = [TitlePlatform.Ps5PlatformId] },
            Snapshot(NewEntitlementId(), conceptId: conceptId, titleMetaName: title, packageType: Ps4PackageType)
                with { PlatformIds = [TitlePlatform.Ps4PlatformId] },
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal([TitlePlatform.Ps4, TitlePlatform.Ps5], game.Platforms);
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
            snapshots, [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal([TitlePlatform.Ps3], game.Platforms);
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
            snapshots, [], NoEditionRanks, NoNameOverrides);

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
            snapshots, [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal([firstConceptId, secondConceptId], game.ConceptIds);
    }

    [Fact]
    public void Canonicalize_AppliesANameOverrideToTheProductItNamesAndToNoOtherUnderTheConcept()
    {
        // Arrange
        var conceptId = NewConceptId();
        var overriddenProductId = NewProductId();
        var otherProductId = NewProductId();
        var overrideName = NewOverrideName();
        var otherTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId(), conceptId: conceptId, productId: overriddenProductId, titleId: Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), titleMetaName: NewGameTitle(), packageType: Ps4PackageType),
            Snapshot(NewEntitlementId(), conceptId: conceptId, productId: otherProductId, titleId: Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), titleMetaName: otherTitle, packageType: Ps4PackageType),
        };
        var overrides = new Dictionary<NameOverrideKey, string>
        {
            [new NameOverrideKey(conceptId, overriddenProductId)] = overrideName,
        };

        // Act
        var games = CanonicalizationService.Canonicalize(snapshots, [], NoEditionRanks, overrides);

        // Assert
        Assert.Equal(
            new[] { otherTitle, overrideName }.Order(StringComparer.Ordinal),
            games.Select(game => game.CanonicalTitle));
    }

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
