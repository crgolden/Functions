namespace Functions.Churches.Extraction;

internal sealed record EnrichmentPartialData(
    string? CanonicalName,
    string? Street,
    string? City,
    string? State,
    string? Zip);
