namespace Functions;

// Single home of the confidence formula. Driven by data already committed to the database, so it is a
// pure function of the church row plus its attribute count — safe to recompute any number of times.
public static class ConfidenceScoreCalculator
{
    private const double CoordinateEpsilon = 1e-9;

    public static decimal Calculate(ConfidenceInputs church, int attributeCount)
    {
        var score = 0m;

        if (!string.IsNullOrWhiteSpace(church.CanonicalName))
        {
            score += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(church.City))
        {
            score += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(church.State))
        {
            score += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(church.Zip))
        {
            score += 0.2m;
        }

        if (Math.Abs(church.Latitude) > CoordinateEpsilon || Math.Abs(church.Longitude) > CoordinateEpsilon)
        {
            score += 0.2m;
        }

        if (!string.IsNullOrWhiteSpace(church.PhoneNumber))
        {
            score += 0.05m;
        }

        if (!string.IsNullOrWhiteSpace(church.Website))
        {
            score += 0.05m;
        }

        if (!string.IsNullOrWhiteSpace(church.EmailAddress))
        {
            score += 0.05m;
        }

        if (church.HasDenomination)
        {
            score += 0.05m;
        }

        if (church.WorshipStyle != 0)
        {
            score += 0.05m;
        }

        score += Math.Min(attributeCount * 0.01m, 0.2m);

        if (church.LastVerifiedAt.HasValue &&
            DateTimeOffset.UtcNow - church.LastVerifiedAt.Value <= TimeSpan.FromDays(365))
        {
            score += 0.1m;
        }

        return Math.Min(score, 1.0m);
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
