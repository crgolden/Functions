namespace Functions.Curator.Library;

public sealed record EntitlementSnapshot(string EntitlementId)
{
    public string? ConceptId { get; init; }

    public string? ProductId { get; init; }

    public string? TitleId { get; init; }

    public string? GameMetaName { get; init; }

    public string? ConceptMetaName { get; init; }

    public string? TitleMetaName { get; init; }

    public string? PackageType { get; init; }

    public bool? Active { get; init; }

    public string? SkuId { get; init; }

    public DateTimeOffset? ActiveDate { get; init; }

    public Uri? TitleImageUrl { get; init; }

    public Uri? GameIconUrl { get; init; }

    public Uri? ConceptIconUrl { get; init; }

    public bool? IsGame { get; init; }

    public IReadOnlyList<string> PlatformIds { get; init; } = [];

    public string Raw { get; init; } = "{}";
}
