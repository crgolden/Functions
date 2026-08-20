namespace Functions.Tests.Unit;

using Curator.Catalog;
using Curator.Library;

[Trait("Category", "Unit")]
public sealed class CanonicalizationServiceTests
{
    private static readonly IReadOnlyDictionary<string, int> NoEditionRanks = new Dictionary<string, int>();
    private static readonly IReadOnlyDictionary<string, string> NoNameOverrides = new Dictionary<string, string>();

    [Theory]
    [InlineData("Bloodborne™", "Bloodborne")]
    [InlineData("ALIENATIONTM", "ALIENATION")]
    [InlineData("Journey®", "Journey")]
    [InlineData("Game  With   Spaces", "Game With Spaces")]
    [InlineData("  Trimmed  ", "Trimmed")]
    public void NormalizeName_StripsTrademarkMarkersAndCollapsesWhitespace(string raw, string expected)
    {
        // Act
        var normalized = CanonicalizationService.NormalizeName(raw);

        // Assert
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void NormalizeName_RemovesTheEmptyParenthesesLeftBehindByStrippingTrademarkLetters()
    {
        // Act
        var normalized = CanonicalizationService.NormalizeName("Game (TM)");

        // Assert
        Assert.Equal("Game", normalized);
    }

    [Fact]
    public void NormalizeName_StripsDiacriticsWithoutDroppingTheBaseLetter()
    {
        // Act
        var normalized = CanonicalizationService.NormalizeName("Pokémon");

        // Assert
        Assert.Equal("Pokemon", normalized);
    }

    [Fact]
    public void EditionRank_ReturnsTheRankOfTheLowestRankedMatchingKeyword()
    {
        // Arrange
        var ranks = new Dictionary<string, int> { ["standard"] = 5, ["game of the year"] = 1 };

        // Act
        var rank = CanonicalizationService.EditionRank("Some Game of the Year Standard Edition", ranks);

        // Assert
        Assert.Equal(1, rank);
    }

    [Fact]
    public void EditionRank_ReturnsTheUnrankedValue_WhenNoKeywordMatches()
    {
        // Arrange
        var ranks = new Dictionary<string, int> { ["deluxe"] = 2 };

        // Act
        var rank = CanonicalizationService.EditionRank("Plain Game", ranks);

        // Assert
        Assert.Equal(CanonicalizationService.UnrankedEdition, rank);
    }

    [Fact]
    public void Canonicalize_ProducesOneGamePerConceptId()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PSGD"),
            Snapshot("e2", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PS4GD"),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        var game = Assert.Single(games);
        Assert.Equal("Bloodborne", game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_PrefersThePs5NativeEditionAsTheWinner()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot("e-ps4", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PS4GD"),
            Snapshot("e-ps5", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PSGD"),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.True(game.NativePs5);
        Assert.Equal("e-ps5", game.WinningEntitlementId);
    }

    [Fact]
    public void Canonicalize_ReportsPs4Eligibility_WhenAnyEntryIsAPs4Edition()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot("e-ps5", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PSGD"),
            Snapshot("e-ps4", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PS4GD"),
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
        var snapshots = new[]
        {
            Snapshot("e-lapsed", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PSGD", active: false),
            Snapshot("e-active", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PS4GD", active: true),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal("e-active", game.WinningEntitlementId);
    }

    [Fact]
    public void Canonicalize_ReportsAGameAsActive_WhenAnyOfItsEntitlementsStillIs()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PSGD", active: false),
            Snapshot("e2", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PS4GD", active: true),
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
        var snapshots = new[] { Snapshot("e1", conceptId: "c1", titleMetaName: "Bloodborne", packageType: "PSGD") };
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "c1" };

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
            Snapshot("e1", conceptId: "c1", titleId: "SUBC00001_00", titleMetaName: "PS Plus", packageType: "PSGD"),
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
            Snapshot("e1", conceptId: "c1", titleMetaName: "Some App", packageType: "PSGD", isGame: false),
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
            Snapshot("e1", conceptId: "c1", titleMetaName: "Some DLC", packageType: "PS4AC"),
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
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c1", titleId: "BLUS30233_00", titleMetaName: "Legacy PS3 Game"),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal("Legacy PS3 Game", game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_DropsAnUnclassifiedEntitlement_WhenEveryClassifiedSiblingOfItsTitleIsNonGame()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot("e-addon", conceptId: "c1", titleId: "CUSA00011_00", titleMetaName: "Theme", packageType: "PS4AL"),
            Snapshot("e-unknown", conceptId: "c2", titleId: "CUSA00011_00", titleMetaName: "Theme"),
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
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c1", gameMetaName: "Netflix", titleMetaName: "Netflix", packageType: "PSGD"),
        };
        var rules = new[] { new ExclusionRule(Guid.Parse("10000000-0000-0000-0000-000000000000"), ExclusionRules.MediaApp, "Netflix") };

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
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c1", titleMetaName: "Wrong PSN Name", packageType: "PSGD"),
        };
        var overrides = new Dictionary<string, string> { ["c1"] = "Corrected Name" };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, overrides));

        // Assert
        Assert.Equal("Corrected Name", game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_FallsThroughAnEmptyOverrideToThePsnSuppliedName()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c1", titleMetaName: "PSN Name", packageType: "PSGD"),
        };
        var overrides = new Dictionary<string, string> { ["c1"] = string.Empty };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, overrides));

        // Assert
        Assert.Equal("PSN Name", game.CanonicalTitle);
    }

    [Fact]
    public void Canonicalize_DropsAnEntitlementWhoseNameNormalisesToNothing()
    {
        // Arrange
        var snapshots = new[] { Snapshot("e1", conceptId: "c1", titleMetaName: "™", packageType: "PSGD") };

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
        var snapshots = new[]
        {
            Snapshot("e1", titleMetaName: "Journey", packageType: "PSGD"),
            Snapshot("e2", titleMetaName: "Journey", packageType: "PS4GD"),
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
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c1", titleMetaName: "Game", packageType: "PSGD")
                with { PlatformIds = ["ps5"] },
            Snapshot("e2", conceptId: "c1", titleMetaName: "Game", packageType: "PS4GD")
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
            Snapshot("e1", conceptId: "c1", titleId: "BLUS30233_00", titleMetaName: "Legacy", packageType: "PSGD"),
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
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c1", titleMetaName: "zelda", packageType: "PSGD"),
            Snapshot("e2", conceptId: "c2", titleMetaName: "Astro Bot", packageType: "PSGD"),
        };

        // Act
        var games = CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides);

        // Assert
        Assert.Equal(["Astro Bot", "zelda"], games.Select(game => game.CanonicalTitle));
    }

    [Fact]
    public void Canonicalize_CollectsEveryConceptIdTheMergedEntriesCarried()
    {
        // Arrange
        var snapshots = new[]
        {
            Snapshot("e1", conceptId: "c-b", productId: "p1", titleMetaName: "Game", packageType: "PSGD"),
            Snapshot("e2", conceptId: "c-a", productId: "p1", titleMetaName: "Game", packageType: "PSGD"),
        };

        // Act
        var game = Assert.Single(CanonicalizationService.Canonicalize(
            snapshots, [], [], NoEditionRanks, NoNameOverrides));

        // Assert
        Assert.Equal(["c-a", "c-b"], game.ConceptIds);
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
