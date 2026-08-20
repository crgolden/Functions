namespace Functions.Curator.Library;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record EntitlementSnapshotRow
{
    private readonly IReadOnlyList<string> _platformIds = [];

    [JsonPropertyName(EntitlementSnapshotColumns.EntitlementId)]
    public string? EntitlementId { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.ConceptId)]
    public string? ConceptId { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.ProductId)]
    public string? ProductId { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.SkuId)]
    public string? SkuId { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.TitleId)]
    public string? TitleId { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.GameMetaName)]
    public string? GameMetaName { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.ConceptMetaName)]
    public string? ConceptMetaName { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.TitleMetaName)]
    public string? TitleMetaName { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.PackageType)]
    public string? PackageType { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.Active)]
    public bool? Active { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.ActiveDate)]
    public DateTimeOffset? ActiveDate { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.TitleImageUrl)]
    public string? TitleImageUrl { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.GameIconUrl)]
    public string? GameIconUrl { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.ConceptIconUrl)]
    public string? ConceptIconUrl { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.IsGame)]
    public bool? IsGame { get; init; }

    [JsonPropertyName(EntitlementSnapshotColumns.PlatformIds)]
    public IReadOnlyList<string> PlatformIds
    {
        get => _platformIds;
        init => _platformIds = value ?? [];
    }

    [JsonPropertyName(EntitlementSnapshotColumns.Raw)]
    public JsonElement Raw { get; init; }

    public static EntitlementSnapshotRow From(EntitlementSnapshot snapshot)
    {
        using var raw = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(snapshot.Raw) ? "{}" : snapshot.Raw);

        return new EntitlementSnapshotRow
        {
            EntitlementId = snapshot.EntitlementId,
            ConceptId = snapshot.ConceptId,
            ProductId = snapshot.ProductId,
            SkuId = snapshot.SkuId,
            TitleId = snapshot.TitleId,
            GameMetaName = snapshot.GameMetaName,
            ConceptMetaName = snapshot.ConceptMetaName,
            TitleMetaName = snapshot.TitleMetaName,
            PackageType = snapshot.PackageType,
            Active = snapshot.Active,
            ActiveDate = snapshot.ActiveDate?.ToUniversalTime(),
            TitleImageUrl = snapshot.TitleImageUrl,
            GameIconUrl = snapshot.GameIconUrl,
            ConceptIconUrl = snapshot.ConceptIconUrl,
            IsGame = snapshot.IsGame,
            PlatformIds = snapshot.PlatformIds,
            Raw = raw.RootElement.Clone(),
        };
    }
}
