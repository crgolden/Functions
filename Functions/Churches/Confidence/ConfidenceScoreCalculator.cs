namespace Functions.Churches.Confidence;

public static class ConfidenceScoreCalculator
{
    internal const decimal CoreFieldWeight = 0.2m;
    internal const decimal SecondarySignalWeight = 0.05m;
    internal const decimal WeightPerAttribute = 0.01m;
    internal const decimal AttributeWeightCap = 0.2m;
    internal const decimal RecentVerificationBonus = 0.1m;
    internal const int RecentVerificationMaxDays = 365;
    internal const decimal MaxScore = 1.0m;

    private const double CoordinateEpsilon = 1e-9;

    public static decimal Calculate(ConfidenceInputs church, int attributeCount)
    {
        var score = 0m;

        if (!string.IsNullOrWhiteSpace(church.CanonicalName))
        {
            score += CoreFieldWeight;
        }

        if (!string.IsNullOrWhiteSpace(church.City))
        {
            score += CoreFieldWeight;
        }

        if (!string.IsNullOrWhiteSpace(church.State))
        {
            score += CoreFieldWeight;
        }

        if (!string.IsNullOrWhiteSpace(church.Zip))
        {
            score += CoreFieldWeight;
        }

        if (Math.Abs(church.Latitude) > CoordinateEpsilon || Math.Abs(church.Longitude) > CoordinateEpsilon)
        {
            score += CoreFieldWeight;
        }

        if (!string.IsNullOrWhiteSpace(church.PhoneNumber))
        {
            score += SecondarySignalWeight;
        }

        if (!string.IsNullOrWhiteSpace(church.Website))
        {
            score += SecondarySignalWeight;
        }

        if (!string.IsNullOrWhiteSpace(church.EmailAddress))
        {
            score += SecondarySignalWeight;
        }

        if (church.HasDenomination)
        {
            score += SecondarySignalWeight;
        }

        if (church.WorshipStyle != 0)
        {
            score += SecondarySignalWeight;
        }

        score += Math.Min(attributeCount * WeightPerAttribute, AttributeWeightCap);

        if (church.LastVerifiedAt.HasValue &&
            DateTimeOffset.UtcNow - church.LastVerifiedAt.Value <= TimeSpan.FromDays(RecentVerificationMaxDays))
        {
            score += RecentVerificationBonus;
        }

        return Math.Min(score, MaxScore);
    }
}

public sealed record ConfidenceInputs(
    string? CanonicalName,
    string? City,
    string? State,
    string? Zip,
    double Latitude,
    double Longitude,
    string? PhoneNumber,
    string? Website,
    string? EmailAddress,
    bool HasDenomination,
    int WorshipStyle,
    DateTimeOffset? LastVerifiedAt);