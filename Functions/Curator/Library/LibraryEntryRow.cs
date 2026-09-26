namespace Functions.Curator.Library;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
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

    public static LibraryEntryRow Create(
        Guid gameId,
        bool nativePs5,
        bool ps4Eligible,
        string? ownedEdition,
        string winningEntitlementId,
        string? productId,
        string? titleId,
        IReadOnlyList<string> platforms,
        bool isActive) => new()
        {
            GameId = gameId,
            OwnedEdition = ownedEdition,
            WinningEntitlementId = winningEntitlementId,
            ProductId = productId,
            TitleId = titleId,
            IsActive = isActive,
            Platforms = OwnedPlatforms(nativePs5, ps4Eligible, platforms),
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
