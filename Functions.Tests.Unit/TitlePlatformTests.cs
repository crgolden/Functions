namespace Functions.Tests.Unit;

using Functions.Curator.Psn;
using static Functions.Tests.Unit.SonyTitleIdPrefixFixtureConstants;

[Trait("Category", "Unit")]
public sealed class TitlePlatformTests
{
    private const string? NoIdentifier = null;

    public static TheoryData<string, string> TitleIdsAndTheirConsoles() => new()
    {
        { Generated.NewTitleId(TitlePlatform.Ps5TitleIdPrefix), TitlePlatform.Ps5 },
        { Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), TitlePlatform.Ps4 },
        { Generated.NewTitleId(Ps3NorthAmericanDiscPrefix), TitlePlatform.Ps3 },
        { Generated.NewTitleId(PsVitaFirstPartyPrefix), TitlePlatform.PsVita },
        { Generated.NewTitleId(PspNorthAmericanDiscPrefix), TitlePlatform.Psp },
    };

    public static TheoryData<string?> TitleIdsNamingNoConsole() =>
        [NoIdentifier, string.Empty, Generated.NewBlankRun(), Generated.NewTitleId(UnassignedPrefix)];

    public static TheoryData<string> NonTitleEntitlementIds() =>
    [
        Generated.NewTitleId(SubscriptionPrefix),
        Generated.NewTitleId(PromotionPrefix),
        Generated.NewTitleId(SystemPrefix),
    ];

    public static TheoryData<string?> RealTitleIdsOrNone() => [Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), NoIdentifier, string.Empty];

    public static TheoryData<string, string> PlatformIdsAndTheirConsoles() => new()
    {
        { TitlePlatform.Ps5PlatformId, TitlePlatform.Ps5 },
        { TitlePlatform.Ps4PlatformId.ToUpperInvariant(), TitlePlatform.Ps4 },
        { TitlePlatform.PsVitaPlatformId, TitlePlatform.PsVita },
    };

    public static TheoryData<string?> PlatformIdsNamingNoConsole() => [Generated.NewPlatformId(), NoIdentifier, string.Empty];

    [Theory]
    [MemberData(nameof(TitleIdsAndTheirConsoles))]
    public void PlatformForTitleId_ResolvesEachConsoleGenerationFromItsPrefix(string titleId, string expected)
    {
        // Act
        var platform = TitlePlatform.PlatformForTitleId(titleId);

        // Assert
        Assert.Equal(expected, platform);
    }

    [Theory]
    [MemberData(nameof(TitleIdsNamingNoConsole))]
    public void PlatformForTitleId_ResolvesNothing_ForAnAbsentOrUnrecognisedPrefix(string? titleId)
    {
        // Act
        var platform = TitlePlatform.PlatformForTitleId(titleId);

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

    [Theory]
    [MemberData(nameof(NonTitleEntitlementIds))]
    public void IsNonTitleEntitlement_ReportsTrue_ForSubscriptionPromotionAndSystemPrefixes(string titleId)
    {
        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(titleId);

        // Assert
        Assert.True(nonTitle);
    }

    [Theory]
    [MemberData(nameof(RealTitleIdsOrNone))]
    public void IsNonTitleEntitlement_ReportsFalse_ForARealTitleOrNoTitleAtAll(string? titleId)
    {
        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(titleId);

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

    [Theory]
    [MemberData(nameof(PlatformIdsNamingNoConsole))]
    public void NormalizePlatformId_DropsAValueThatNamesNoConsole(string? raw)
    {
        // Act
        var platform = TitlePlatform.NormalizePlatformId(raw);

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
