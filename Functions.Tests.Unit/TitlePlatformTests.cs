namespace Functions.Tests.Unit;

using Curator.Psn;

[Trait("Category", "Unit")]
public sealed class TitlePlatformTests
{
    [Theory]
    [InlineData("PPSA01234_00", "PS5")]
    [InlineData("CUSA00011_00", "PS4")]
    [InlineData("BLUS30233_00", "PS3")]
    [InlineData("PCSA00021_00", "PSVITA")]
    [InlineData("ULUS10041_00", "PSP")]
    public void PlatformForTitleId_ResolvesEachConsoleGenerationFromItsPrefix(string titleId, string expected)
    {
        // Act
        var platform = TitlePlatform.PlatformForTitleId(titleId);

        // Assert
        Assert.Equal(expected, platform);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ZZZZ00001_00")]
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
        // Act
        var platform = TitlePlatform.PlatformForTitleId("SUBC00001_00");

        // Assert
        Assert.Null(platform);
    }

    [Fact]
    public void PlatformForTitleId_MatchesCaseInsensitively()
    {
        // Act
        var platform = TitlePlatform.PlatformForTitleId("ppsa01234_00");

        // Assert
        Assert.Equal("PS5", platform);
    }

    [Theory]
    [InlineData("SUBC00001_00")]
    [InlineData("NPIA00001_00")]
    [InlineData("PSNP00001_00")]
    public void IsNonTitleEntitlement_ReportsTrue_ForSubscriptionPromotionAndSystemPrefixes(string titleId)
    {
        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(titleId);

        // Assert
        Assert.True(nonTitle);
    }

    [Theory]
    [InlineData("CUSA00011_00")]
    [InlineData(null)]
    [InlineData("")]
    public void IsNonTitleEntitlement_ReportsFalse_ForARealTitleOrNoTitleAtAll(string? titleId)
    {
        // Act
        var nonTitle = TitlePlatform.IsNonTitleEntitlement(titleId);

        // Assert
        Assert.False(nonTitle);
    }

    [Theory]
    [InlineData("ps5", "PS5")]
    [InlineData("PS4", "PS4")]
    [InlineData("psvita", "PSVITA")]
    public void NormalizePlatformId_UppercasesARecognisedConsoleValue(string raw, string expected)
    {
        // Act
        var platform = TitlePlatform.NormalizePlatformId(raw);

        // Assert
        Assert.Equal(expected, platform);
    }

    [Theory]
    [InlineData("xperia")]
    [InlineData(null)]
    [InlineData("")]
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
        // Act
        var platform = TitlePlatform.PlatformForTitleId("PS");

        // Assert
        Assert.Null(platform);
    }
}
