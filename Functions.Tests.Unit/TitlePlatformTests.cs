namespace Functions.Tests.Unit;

using Functions.Curator.Psn;
using static Functions.Tests.Unit.SonyTitleIdPrefixFixtureConstants;

[Trait("Category", "Unit")]
public sealed class TitlePlatformTests
{
    private const string? NoIdentifier = null;

    public static TheoryData<string, string> PlatformIdsAndTheirConsoles() => new()
    {
        { TitlePlatform.Ps5PlatformId, TitlePlatform.Ps5 },
        { TitlePlatform.Ps4PlatformId.ToUpperInvariant(), TitlePlatform.Ps4 },
        { TitlePlatform.PsVitaPlatformId, TitlePlatform.PsVita },
    };

    [Fact]
    public void PlatformForTitleId_Ps5Prefix_ResolvesPs5()
    {
        // Arrange
        var ps5TitleId = Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix);

        // Act
        var platform = TitlePlatform.PlatformForTitleId(ps5TitleId);

        // Assert
        Assert.Equal(TitlePlatform.Ps5, platform);
    }

    [Fact]
    public void PlatformForTitleId_Ps4Prefix_ResolvesPs4()
    {
        // Arrange
        var ps4TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);

        // Act
        var platform = TitlePlatform.PlatformForTitleId(ps4TitleId);

        // Assert
        Assert.Equal(TitlePlatform.Ps4, platform);
    }

    [Fact]
    public void PlatformForTitleId_Ps3Prefix_ResolvesPs3()
    {
        // Arrange
        var ps3TitleId = Generated.NewTitleId(Ps3NorthAmericanDiscPrefix);

        // Act
        var platform = TitlePlatform.PlatformForTitleId(ps3TitleId);

        // Assert
        Assert.Equal(TitlePlatform.Ps3, platform);
    }

    [Fact]
    public void PlatformForTitleId_PsVitaPrefix_ResolvesPsVita()
    {
        // Arrange
        var psVitaTitleId = Generated.NewTitleId(PsVitaFirstPartyPrefix);

        // Act
        var platform = TitlePlatform.PlatformForTitleId(psVitaTitleId);

        // Assert
        Assert.Equal(TitlePlatform.PsVita, platform);
    }

    [Fact]
    public void PlatformForTitleId_PspPrefix_ResolvesPsp()
    {
        // Arrange
        var pspTitleId = Generated.NewTitleId(PspNorthAmericanDiscPrefix);

        // Act
        var platform = TitlePlatform.PlatformForTitleId(pspTitleId);

        // Assert
        Assert.Equal(TitlePlatform.Psp, platform);
    }

    [Fact]
    public void PlatformForTitleId_ResolvesNothing_ForAnAbsentTitleId()
    {
        // Act
        var platform = TitlePlatform.PlatformForTitleId(NoIdentifier);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void PlatformForTitleId_ResolvesNothing_ForABlankTitleId()
    {
        // Arrange
        var blankTitleId = Generated.NewBlankRun();

        // Act
        var platform = TitlePlatform.PlatformForTitleId(blankTitleId);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void PlatformForTitleId_ResolvesNothing_ForAnUnassignedPrefix()
    {
        // Arrange
        var unassignedTitleId = Generated.NewTitleId(UnassignedPrefix);

        // Act
        var platform = TitlePlatform.PlatformForTitleId(unassignedTitleId);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void PlatformForTitleId_ResolvesNothing_ForANonTitleEntitlement()
    {
        // Arrange
        var subscriptionTitleId = Generated.NewTitleId(SubscriptionPrefix);

        // Act
        var platform = TitlePlatform.PlatformForTitleId(subscriptionTitleId);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void PlatformForTitleId_MatchesCaseInsensitively()
    {
        // Arrange
        var lowercasePs5TitleId = Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix).ToLowerInvariant();

        // Act
        var platform = TitlePlatform.PlatformForTitleId(lowercasePs5TitleId);

        // Assert
        Assert.Equal(TitlePlatform.Ps5, platform);
    }

    [Fact]
    public void IsNonTitleEntitlement_ReportsTrue_ForASubscriptionPrefix()
    {
        // Arrange
        var subscriptionTitleId = Generated.NewTitleId(SubscriptionPrefix);

        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(subscriptionTitleId);

        // Assert
        Assert.True(nonTitle);
    }

    [Fact]
    public void IsNonTitleEntitlement_ReportsTrue_ForAPromotionPrefix()
    {
        // Arrange
        var promotionTitleId = Generated.NewTitleId(PromotionPrefix);

        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(promotionTitleId);

        // Assert
        Assert.True(nonTitle);
    }

    [Fact]
    public void IsNonTitleEntitlement_ReportsTrue_ForASystemPrefix()
    {
        // Arrange
        var systemTitleId = Generated.NewTitleId(SystemPrefix);

        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(systemTitleId);

        // Assert
        Assert.True(nonTitle);
    }

    [Fact]
    public void IsNonTitleEntitlement_ReportsFalse_ForARealTitle()
    {
        // Arrange
        var realTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);

        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(realTitleId);

        // Assert
        Assert.False(nonTitle);
    }

    [Fact]
    public void IsNonTitleEntitlement_ReportsFalse_ForNoTitleAtAll()
    {
        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(NoIdentifier);

        // Assert
        Assert.False(nonTitle);
    }

    [Fact]
    public void IsNonTitleEntitlement_ReportsFalse_ForABlankTitleId()
    {
        // Arrange
        var blankTitleId = Generated.NewBlank();

        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(blankTitleId);

        // Assert
        Assert.False(nonTitle);
    }

    [Theory]
    [MemberData(nameof(PlatformIdsAndTheirConsoles))]
    public void NormalizePlatformId_UppercasesARecognisedConsoleValue(string raw, string expected)
    {
        // Act
        var platform = TitlePlatform.NormalizePlatformId(raw);

        // Assert
        Assert.Equal(expected, platform);
    }

    [Fact]
    public void NormalizePlatformId_DropsAnAbsentValue()
    {
        // Act
        var platform = TitlePlatform.NormalizePlatformId(NoIdentifier);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void NormalizePlatformId_DropsABlankValue()
    {
        // Arrange
        var blankPlatformId = Generated.NewBlank();

        // Act
        var platform = TitlePlatform.NormalizePlatformId(blankPlatformId);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void NormalizePlatformId_DropsAValueThatNamesNoConsole()
    {
        // Arrange
        var unrecognisedPlatformId = Generated.NewPlatformId();

        // Act
        var platform = TitlePlatform.NormalizePlatformId(unrecognisedPlatformId);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void PlatformForTitleId_ResolvesAPrefixShorterThanFourCharactersWithoutThrowing()
    {
        // Arrange
        var truncatedTitleId = Generated.NewTextShorterThanATitleIdPrefix();

        // Act
        var platform = TitlePlatform.PlatformForTitleId(truncatedTitleId);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void ConsoleNames_KeepTheSpellingsLibraryEntriesStore()
    {
        // Act
        string[] consoles = [TitlePlatform.Ps5, TitlePlatform.Ps4, TitlePlatform.Ps3, TitlePlatform.PsVita, TitlePlatform.Psp];

        // Assert
        Assert.Equal(["PS5", "PS4", "PS3", "PSVITA", "PSP"], consoles);
    }
}
