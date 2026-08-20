namespace Functions.Curator.Psn;

public sealed record Entitlement
{
    public string? EntitlementId { get; init; }

    public string? Name { get; init; }

    public string? TitleId { get; init; }

    public string? ConceptId { get; init; }

    public string? ProductId { get; init; }

    public string? SkuId { get; init; }

    public string? PackageType { get; init; }

    public string? GameType { get; init; }

    public bool? Active { get; init; }

    public DateTimeOffset? ActiveDate { get; init; }

    public string? ImageUrl { get; init; }

    public string? TitleImageUrl { get; init; }

    public string? GameIconUrl { get; init; }

    public string? ConceptIconUrl { get; init; }

    public bool? IsGame { get; init; }

    public IReadOnlyList<string> PlatformIds { get; init; } = [];

    public string? GameMetaName { get; init; }

    public string? ConceptMetaName { get; init; }

    public string? TitleMetaName { get; init; }

    public string Raw { get; init; } = "{}";
}
