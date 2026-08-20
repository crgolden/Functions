namespace Functions.Tests.Unit;

using System.Globalization;

[Trait("Category", "Unit")]
public sealed class ConfidenceScoreCalculatorTests
{
    private const int NoAttributes = 0;

    private const int RecentVerificationMaxDays = 365;

    [Fact]
    public void Calculate_NothingPresent_ReturnsZero()
    {
        Assert.Equal(0m, ConfidenceScoreCalculator.Calculate(Empty(), NoAttributes));
    }

    [Fact]
    public void Calculate_CoreFieldsAndCoordinates_ReturnsOne()
    {
        // Arrange
        var inputs = Empty() with
        {
            CanonicalName = NewChurchName(),
            City = NewCity(),
            State = NewStateCode(),
            Zip = NewZip(),
            Latitude = NewLatitude(),
            Longitude = NewLongitude(),
        };

        // Act
        var score = ConfidenceScoreCalculator.Calculate(inputs, NoAttributes);

        // Assert
        Assert.Equal(1.0m, score);
    }

    [Fact]
    public void Calculate_AttributeCount_CapsAtPointTwo()
    {
        // Arrange
        var inputs = Empty() with { CanonicalName = NewChurchName() };
        var attributeCountFarAboveCap = Random.Shared.Next(50, 500);

        // Act
        var score = ConfidenceScoreCalculator.Calculate(inputs, attributeCountFarAboveCap);

        // Assert
        Assert.Equal(0.4m, score);
    }

    [Fact]
    public void Calculate_RecentVerification_AddsBonus()
    {
        // Arrange
        var namedInputs = Empty() with { CanonicalName = NewChurchName() };
        var daysSinceRecentVerification = Random.Shared.Next(1, RecentVerificationMaxDays - 1);
        var recentlyVerified = namedInputs with
        {
            LastVerifiedAt = DateTimeOffset.UtcNow.AddDays(-daysSinceRecentVerification),
        };

        // Act
        var score = ConfidenceScoreCalculator.Calculate(recentlyVerified, NoAttributes);

        // Assert
        Assert.Equal(0.3m, score);
    }

    [Fact]
    public void Calculate_StaleVerification_AddsNoBonus()
    {
        // Arrange
        var namedInputs = Empty() with { CanonicalName = NewChurchName() };
        var daysSinceStaleVerification = Random.Shared.Next(RecentVerificationMaxDays + 1, RecentVerificationMaxDays + 500);
        var staleVerified = namedInputs with
        {
            LastVerifiedAt = DateTimeOffset.UtcNow.AddDays(-daysSinceStaleVerification),
        };

        // Act
        var score = ConfidenceScoreCalculator.Calculate(staleVerified, NoAttributes);

        // Assert
        Assert.Equal(0.2m, score);
    }

    [Fact]
    public void Calculate_SecondarySignals_AddSmallIncrements()
    {
        // Arrange
        var inputs = Empty() with
        {
            CanonicalName = NewChurchName(),
            PhoneNumber = NewPhoneNumber(),
            Website = NewWebsite(),
            EmailAddress = NewEmailAddress(),
            HasDenomination = true,
            WorshipStyle = NewWorshipStyle(),
        };

        // Act
        var score = ConfidenceScoreCalculator.Calculate(inputs, NoAttributes);

        // Assert
        Assert.Equal(0.45m, score);
    }

    private static ConfidenceInputs Empty() =>
        new(null, null, null, null, 0, 0, null, null, null, false, 0, null);

    private static string NewChurchName() => $"Church{Guid.NewGuid():N}";

    private static string NewCity() => $"City{Guid.NewGuid():N}";

    private static string NewStateCode() =>
        $"{(char)Random.Shared.Next('A', 'Z' + 1)}{(char)Random.Shared.Next('A', 'Z' + 1)}";

    private static string NewZip() => Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);

    private static int NewWorshipStyle() => Random.Shared.Next(1, 6);

    private static string NewPhoneNumber() =>
        $"+1{Random.Shared.NextInt64(2000000000L, 9999999999L).ToString(CultureInfo.InvariantCulture)}";

    private static string NewWebsite() => $"https://{Guid.NewGuid():N}.example";

    private static string NewEmailAddress() => $"{Guid.NewGuid():N}@{Guid.NewGuid():N}.example";

    private static double NewLatitude() => Math.Round((Random.Shared.NextDouble() * 40) + 1, 4);

    private static double NewLongitude() => -Math.Round((Random.Shared.NextDouble() * 100) + 1, 4);
}
