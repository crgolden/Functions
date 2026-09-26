namespace Functions.Churches.Extraction;

internal sealed record EnrichedData(
    string? CanonicalName,
    string? Street,
    string? City,
    string? State,
    string? Zip,
    int WorshipStyle,
    string PrimaryLanguage,
    bool? AcceptsLGBTQ,
    bool? WheelchairAccessible,
    bool? HasNursery,
    bool? HasYouthProgram,
    string? Denomination,
    IReadOnlyList<ServiceScheduleData> ServiceSchedules,
    IReadOnlyList<MinistryData> Ministries,
    IReadOnlyList<CampusData> Campuses);
