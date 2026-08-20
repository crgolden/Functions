namespace Functions.Curator.Catalog;

public sealed record CanonicalGame(
    string CanonicalTitle,
    bool NativePs5,
    bool Ps4Eligible,
    string? Franchise,
    string? ProductId,
    IReadOnlyList<string> ConceptIds,
    string WinningEntitlementId)
{
    public bool Active { get; init; } = true;

    public string? WinningTitleId { get; init; }

    public IReadOnlyList<string> Platforms { get; init; } = [];
}
