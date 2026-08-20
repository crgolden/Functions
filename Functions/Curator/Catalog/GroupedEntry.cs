namespace Functions.Curator.Catalog;

public sealed record GroupedEntry(
    string Name,
    string? PackageType,
    string? ConceptId,
    string? ProductId,
    string EntitlementId,
    bool? Active = null,
    string? TitleId = null)
{
    public IReadOnlySet<string> Platforms { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
