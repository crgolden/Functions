namespace Functions.Tests.Unit;

using Functions.Churches.Confidence;
using static Functions.Churches.Confidence.ConfidenceScoreCalculator;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class ConfidenceScoreCalculatorTests
{
    private const int NoAttributes = 0;

    [Fact]
    public void Calculate_NothingPresent_ReturnsZero()
    {
        // Act
        var score = ConfidenceScoreCalculator.Calculate(Empty(), NoAttributes);

        // Assert
        Assert.Equal(0m, score);
    }

    [Fact]
    public void Calculate_CoreFieldsAndCoordinates_ReturnsOne()
    {
        // Arrange
        var inputs = Empty() with
        {
            CanonicalName = NewChurchName(),
            City = NewCity(),
            State = NewStateCodeText(),
            Zip = NewZip(),
            Latitude = NewScoredLatitude(),
            Longitude = NewScoredLongitude(),
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
        Assert.Equal(CoreFieldWeight + AttributeWeightCap, score);
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
        Assert.Equal(CoreFieldWeight + RecentVerificationBonus, score);
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
        Assert.Equal(CoreFieldWeight, score);
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
            WorshipStyle = NewWorshipStyleCodeOtherThanUnknown(),
        };

        // Act
        var score = ConfidenceScoreCalculator.Calculate(inputs, NoAttributes);

        // Assert
        Assert.Equal(
            CoreFieldWeight + SecondarySignalWeight + SecondarySignalWeight + SecondarySignalWeight + SecondarySignalWeight + SecondarySignalWeight,
            score);
    }

    private static ConfidenceInputs Empty() =>
        new(null, null, null, null, 0, 0, null, null, null, false, 0, null);
}
