namespace Functions.Churches.Confidence;

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
