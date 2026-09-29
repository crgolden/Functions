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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps5PackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps4PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), TitleMetaName = soundtrackTitle, PackageType = Ps4PackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), TitleMetaName = gameTitle, PackageType = Ps4PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), TitleMetaName = title, PackageType = Ps4PackageType }
                with { PlatformIds = [TitlePlatform.Ps4PlatformId] },
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleId = Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix), TitleMetaName = title, PackageType = Ps5PackageType }
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), TitleMetaName = $"{title} {upgradedKeyword}", PackageType = Ps4PackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), TitleMetaName = $"{title} {baseKeyword}", PackageType = Ps4PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps4PackageType },
            Snapshot(expectedWinningEntitlementId) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps5PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps5PackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps4PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps5PackageType, Active = false },
            Snapshot(expectedWinningEntitlementId) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps4PackageType, Active = true },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps5PackageType, Active = false },
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps4PackageType, Active = true },
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
            Snapshot(NewEntitlementId()) with { ConceptId = excludedConceptId, TitleMetaName = NewGameTitle(), PackageType = Ps5PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleId = SubscriptionTitleId, TitleMetaName = NewGameTitle(), PackageType = Ps5PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleMetaName = NewGameTitle(), PackageType = Ps5PackageType, IsGame = false },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleMetaName = NewGameTitle(), PackageType = AddOnPackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleId = Ps3TitleId, TitleMetaName = title },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleId = Ps4TitleId, TitleMetaName = sharedTitle, PackageType = AddOnLicencePackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleId = Ps4TitleId, TitleMetaName = sharedTitle },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleMetaName = title, PackageType = ContentKinds.MediaAppPackageType, IsGame = false },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleMetaName = NewGameTitle(), PackageType = Ps5PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleId = Ps3TitleId, TitleMetaName = NewGameTitle() },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleId = Ps4TitleId, TitleMetaName = sharedTitle, PackageType = ContentKinds.MediaAppPackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleId = Ps4TitleId, TitleMetaName = sharedTitle },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, ProductId = productId, TitleMetaName = NewGameTitle(), PackageType = Ps5PackageType },
        };
        var overrides = new Dictionary<NameOverrideKey, string> { [new NameOverrideKey(conceptId, productId)] = expectedTitle };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], NoEditionRanks, overrides));

        // Assert
        Assert.Equal(expectedTitle, game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_FallsThroughABlankOverrideToThePsnSuppliedName()
    {
        // Arrange
        var conceptId = NewConceptId();
        var productId = NewProductId();
        var psnSuppliedTitle = NewGameTitle();
        var snapshots = new[]
        {
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, ProductId = productId, TitleMetaName = psnSuppliedTitle, PackageType = Ps5PackageType },
        };
        var overrides = new Dictionary<NameOverrideKey, string> { [new NameOverrideKey(conceptId, productId)] = Generated.NewBlank() };

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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleMetaName = TrademarkSign, PackageType = Ps5PackageType },
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
            Snapshot(NewEntitlementId()) with { TitleMetaName = sharedTitle, PackageType = Ps5PackageType },
            Snapshot(NewEntitlementId()) with { TitleMetaName = sharedTitle, PackageType = Ps4PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps5PackageType }
                with { PlatformIds = [TitlePlatform.Ps5PlatformId] },
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, TitleMetaName = title, PackageType = Ps4PackageType }
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleId = Ps3TitleId, TitleMetaName = NewGameTitle(), PackageType = Ps5PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleMetaName = upperCaseSecondTitle, PackageType = Ps5PackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = NewConceptId(), TitleMetaName = lowerCaseFirstTitle, PackageType = Ps5PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = secondConceptId, ProductId = sharedProductId, TitleMetaName = sharedTitle, PackageType = Ps5PackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = firstConceptId, ProductId = sharedProductId, TitleMetaName = sharedTitle, PackageType = Ps5PackageType },
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
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, ProductId = overriddenProductId, TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), TitleMetaName = NewGameTitle(), PackageType = Ps4PackageType },
            Snapshot(NewEntitlementId()) with { ConceptId = conceptId, ProductId = otherProductId, TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), TitleMetaName = otherTitle, PackageType = Ps4PackageType },
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

    private static EntitlementSnapshot Snapshot(string entitlementId) => new(entitlementId) { Active = true };
}
