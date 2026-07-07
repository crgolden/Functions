namespace Functions.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class ConfidenceScoreCalculatorTests
{
    [Fact]
    public void Calculate_NothingPresent_ReturnsZero()
    {
        Assert.Equal(0m, ConfidenceScoreCalculator.Calculate(Empty(), 0));
    }

    [Fact]
    public void Calculate_CoreFieldsAndCoordinates_ReturnsOne()
    {
        // name + city + state + zip (0.2 each) + coordinates (0.2) = 1.0
        var inputs = Empty() with
        {
            CanonicalName = "Grace",
            City = "Phoenix",
            State = "AZ",
            Zip = "85001",
            Latitude = 33.4,
            Longitude = -112.0,
        };

        Assert.Equal(1.0m, ConfidenceScoreCalculator.Calculate(inputs, 0));
    }

    [Fact]
    public void Calculate_AttributeCount_CapsAtPointTwo()
    {
        var inputs = Empty() with { CanonicalName = "Grace" }; // 0.2

        // 100 attributes would be +1.0 uncapped; capped at +0.2 → 0.4 total
        Assert.Equal(0.4m, ConfidenceScoreCalculator.Calculate(inputs, 100));
    }

    [Fact]
    public void Calculate_RecentVerification_AddsBonus()
    {
        var baseInputs = Empty() with { CanonicalName = "Grace" }; // 0.2
        var recent = baseInputs with { LastVerifiedAt = DateTimeOffset.UtcNow.AddDays(-10) };
        var stale = baseInputs with { LastVerifiedAt = DateTimeOffset.UtcNow.AddDays(-400) };

        Assert.Equal(0.3m, ConfidenceScoreCalculator.Calculate(recent, 0));
        Assert.Equal(0.2m, ConfidenceScoreCalculator.Calculate(stale, 0));
    }

    [Fact]
    public void Calculate_SecondarySignals_AddSmallIncrements()
    {
        var inputs = Empty() with
        {
            CanonicalName = "Grace", // 0.2
            PhoneNumber = "+16025551212", // 0.05
            Website = "https://grace.example", // 0.05
            EmailAddress = "info@grace.example", // 0.05
            HasDenomination = true, // 0.05
            WorshipStyle = 2, // 0.05
        };

        Assert.Equal(0.45m, ConfidenceScoreCalculator.Calculate(inputs, 0));
    }

    private static ConfidenceInputs Empty() =>
        new(null, null, null, null, 0, 0, null, null, null, false, 0, null);
}
