namespace Functions.Churches;

using System.Diagnostics.CodeAnalysis;

public sealed record GeocodingRequest(
    Guid CrawlSourceId,
    string? CanonicalName,
    string? Street,
    string? City,
    string? State,
    string? Zip,
    string? PhoneNumber,
    string? Website,
    string? EmailAddress,
    int WorshipStyle,
    string PrimaryLanguage,
    bool? AcceptsLGBTQ,
    bool? WheelchairAccessible,
    bool? HasNursery,
    bool? HasYouthProgram,
    decimal Confidence,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? DenominationName = null)
{
    [AllowNull]
    public IReadOnlyList<ChurchAttributeData> Attributes { get => field; init => field = value ?? []; } = [];

    [AllowNull]
    public IReadOnlyList<ServiceScheduleData> ServiceSchedules { get => field; init => field = value ?? []; } = [];

    [AllowNull]
    public IReadOnlyList<MinistryData> Ministries { get => field; init => field = value ?? []; } = [];

    [AllowNull]
    public IReadOnlyList<CampusData> Campuses { get => field; init => field = value ?? []; } = [];
}
