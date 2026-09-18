namespace Functions.Tests.Unit;

using Curator.Psn;
using TestSupport;
using static SonyTitleIdPrefixFixtureConstants;

[Trait("Category", "Unit")]
public sealed class TitlePlatformTests
{
    public static TheoryData<string, string> TitleIdsAndTheirConsoles() => new()
    {
        { TestValues.NewPs5TitleId(), TitlePlatform.Ps5 },
        { TestValues.NewTitleId(), TitlePlatform.Ps4 },
        { TestValues.NewTitleIdWithPrefix(Ps3NorthAmericanDiscPrefix), TitlePlatform.Ps3 },
        { TestValues.NewTitleIdWithPrefix(PsVitaFirstPartyPrefix), TitlePlatform.PsVita },
        { TestValues.NewTitleIdWithPrefix(PspNorthAmericanDiscPrefix), TitlePlatform.Psp },
    };

    public static TheoryData<string?> TitleIdsNamingNoConsole() =>
        [null, string.Empty, TestValues.NewBlankRun(), TestValues.NewTitleIdWithPrefix(UnassignedPrefix)];

    public static TheoryData<string> NonTitleEntitlementIds() =>
    [
        TestValues.NewTitleIdWithPrefix(SubscriptionPrefix),
        TestValues.NewTitleIdWithPrefix(PromotionPrefix),
        TestValues.NewTitleIdWithPrefix(SystemPrefix),
    ];

    public static TheoryData<string?> RealTitleIdsOrNone() => [TestValues.NewTitleId(), null, string.Empty];

    public static TheoryData<string, string> PlatformIdsAndTheirConsoles() => new()
    {
        { TitlePlatform.Ps5PlatformId, TitlePlatform.Ps5 },
        { TitlePlatform.Ps4PlatformId.ToUpperInvariant(), TitlePlatform.Ps4 },
        { TitlePlatform.PsVitaPlatformId, TitlePlatform.PsVita },
    };

    public static TheoryData<string?> PlatformIdsNamingNoConsole() => [TestValues.NewPlatformId(), null, string.Empty];

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
        var subscriptionTitleId = TestValues.NewTitleIdWithPrefix(SubscriptionPrefix);

        // Act
        var platform = TitlePlatform.PlatformForTitleId(subscriptionTitleId);

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void PlatformForTitleId_MatchesCaseInsensitively()
    {
        // Arrange
        var lowercasePs5TitleId = TestValues.NewPs5TitleId().ToLowerInvariant();

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
        var truncatedTitleId = TestValues.NewTextShorterThanATitleIdPrefix();

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
