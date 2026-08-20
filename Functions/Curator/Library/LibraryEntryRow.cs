namespace Functions.Curator.Library;

using System.Text.Json.Serialization;

public sealed record LibraryEntryRow
{
    private readonly IReadOnlyList<string> _platforms = [];

    [JsonPropertyName("game_id")]
    public Guid GameId { get; init; }

    [JsonPropertyName("native_ps5")]
    public bool NativePs5 { get; init; }

    [JsonPropertyName("ps4_eligible")]
    public bool Ps4Eligible { get; init; }

    [JsonPropertyName("owned_edition")]
    public string? OwnedEdition { get; init; }

    [JsonPropertyName("winning_entitlement_id")]
    required public string WinningEntitlementId { get; init; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; init; }

    [JsonPropertyName("title_id")]
    public string? TitleId { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string> Platforms
    {
        get => _platforms;
        init => _platforms = value ?? [];
    }

    public static LibraryEntryRow Create(
        string gameId,
        bool nativePs5,
        bool ps4Eligible,
        string? ownedEdition,
        string winningEntitlementId,
        string? productId,
        string? titleId,
        IReadOnlyList<string> platforms,
        bool isActive) => new()
        {
            GameId = Guid.Parse(gameId),
            NativePs5 = nativePs5,
            Ps4Eligible = ps4Eligible,
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
            owned.Add("PS5");
        }

        if (ps4Eligible)
        {
            owned.Add("PS4");
        }

        foreach (var platform in platforms.Where(platform => !owned.Contains(platform, StringComparer.Ordinal)))
        {
            owned.Add(platform);
        }

        return owned;
    }
}
