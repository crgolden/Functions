namespace Functions.Curator.Library;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Functions.Curator.Catalog;
using Functions.Curator.Psn;
using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LibraryEntryRow
{
    [JsonPropertyName("game_id")]
    public Guid GameId { get; init; }

    [JsonPropertyName("owned_edition")]
    public string? OwnedEdition { get; init; }

    [JsonPropertyName("winning_entitlement_id")]
    public required string WinningEntitlementId { get; init; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; init; }

    [JsonPropertyName("title_id")]
    public string? TitleId { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("platforms")]
    [AllowNull]
    public IReadOnlyList<string> Platforms { get => field; init => field = value ?? []; } = [];

    public static LibraryEntryRow ForCanonicalGame(Guid gameId, CanonicalGame game) => new()
    {
        GameId = gameId,
        OwnedEdition = game.CanonicalTitle,
        WinningEntitlementId = game.WinningEntitlementId,
        ProductId = game.ProductId,
        TitleId = game.WinningTitleId,
        IsActive = game.Active,
        Platforms = OwnedPlatforms(game.NativePs5, game.Ps4Eligible, game.Platforms),
    };

    private static List<string> OwnedPlatforms(
        bool nativePs5,
        bool ps4Eligible,
        IReadOnlyList<string> platforms)
    {
        var owned = new List<string>();
        if (nativePs5)
        {
            owned.Add(TitlePlatform.Ps5);
        }

        if (ps4Eligible)
        {
            owned.Add(TitlePlatform.Ps4);
        }

        foreach (var platform in platforms.Where(platform => !owned.Contains(platform, StringComparer.Ordinal)))
        {
            owned.Add(platform);
        }

        return owned;
    }
}
