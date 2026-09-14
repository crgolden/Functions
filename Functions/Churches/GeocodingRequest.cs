namespace Functions.Churches;

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
    public IReadOnlyList<ChurchAttributeData> Attributes
    {
        get => field;
        init => field = value ?? [];
    } = [];

    public IReadOnlyList<ServiceScheduleData> ServiceSchedules
    {
        get => field;
        init => field = value ?? [];
    } = [];

    public IReadOnlyList<MinistryData> Ministries
    {
        get => field;
        init => field = value ?? [];
    } = [];

    public IReadOnlyList<CampusData> Campuses
    {
        get => field;
        init => field = value ?? [];
    } = [];
}

public sealed record ChurchAttributeData(string Key, string Value, string Source, decimal Confidence);

public sealed record ServiceScheduleData(byte DayOfWeek, string StartTime, string? Description);

public sealed record MinistryData(string Name, string? Description);

public sealed record CampusData(
    string Name,
    string? Street,
    string City,
    string State,
    string Zip,
    decimal? Latitude = null,
    decimal? Longitude = null);
