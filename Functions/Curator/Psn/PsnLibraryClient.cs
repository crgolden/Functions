namespace Functions.Curator.Psn;

using System.Globalization;
using System.Text.Json;

public sealed class PsnLibraryClient : IPsnLibraryClient
{
    public const int PageSize = 200;

#pragma warning disable S1075 // fixed PSN endpoint, not environment-configurable
    internal const string EntitlementsUrl =
        "https://m.np.playstation.com/api/entitlement/v2/users/me/internal/entitlements";
#pragma warning restore S1075

    internal const string EntitlementTypes = "1,2,3,4,5";

    internal const string RequestedFields =
        "titleMeta,gameMeta,conceptMeta,rewardMeta,rewardMeta.retentionPolicy,rewardMeta.rewardMembershipType";

    private const string AllTitleIdsRequireABlankTitleId = "";

    public Task<IReadOnlyList<Entitlement>> EntitlementsAsync(
        PsnSession session,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        session.RunWithReauthAsync(
            () => EntitlementsCoreAsync(session, limit, cancellationToken), cancellationToken);

    private static Entitlement MapEntitlement(JsonElement entry)
    {
        var payload = entry.Deserialize<PsnEntitlementPayload>() ?? new PsnEntitlementPayload();
        var gameMetaName = payload.GameMeta?.Name;
        var titleMetaName = payload.TitleMeta?.Name;
        var titleImageUrl = payload.TitleMeta?.ImageUrl;
        var gameIconUrl = payload.GameMeta?.IconUrl;

        return new Entitlement
        {
            EntitlementId = payload.Id,
            Name = FirstNonEmpty(gameMetaName, titleMetaName),
            TitleId = payload.TitleMeta?.TitleId,
            ConceptId = payload.ConceptMeta?.ConceptId,
            ProductId = payload.ProductId,
            SkuId = payload.SkuId,
            PackageType = payload.GameMeta?.PackageType,
            GameType = payload.GameMeta?.Type,
            Active = payload.ActiveFlag,
            ActiveDate = payload.ActiveDate?.ToUniversalTime(),
            ImageUrl = FirstNonEmpty(titleImageUrl, gameIconUrl),
            TitleImageUrl = titleImageUrl,
            GameIconUrl = gameIconUrl,
            ConceptIconUrl = payload.ConceptMeta?.IconUrl,
            IsGame = payload.IsGame,
            PlatformIds = payload.EntitlementAttributes
                .Select(attribute => attribute.PlatformId)
                .OfType<string>()
                .Where(platformId => !string.IsNullOrWhiteSpace(platformId))
                .ToList(),
            GameMetaName = gameMetaName,
            ConceptMetaName = payload.ConceptMeta?.Name,
            TitleMetaName = titleMetaName,
            Raw = entry.GetRawText(),
        };
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static async Task<IReadOnlyList<Entitlement>> EntitlementsCoreAsync(
        PsnSession session,
        int? limit,
        CancellationToken cancellationToken)
    {
        var entitlements = new List<Entitlement>();
        var offset = 0;

        while (limit is null || entitlements.Count < limit.Value)
        {
            var pageLimit = limit is null ? PageSize : Math.Min(PageSize, limit.Value - entitlements.Count);
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entitlementType"] = EntitlementTypes,
                ["fields"] = RequestedFields,
                ["titleId"] = AllTitleIdsRequireABlankTitleId,
                ["limit"] = pageLimit.ToString(CultureInfo.InvariantCulture),
                ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            };

            using var response = await session
                .GetAsync(EntitlementsUrl, query, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>(body) ?? new PsnEntitlementsResponse();
            if (page.Entitlements.Count == 0)
            {
                break;
            }

            foreach (var entry in page.Entitlements)
            {
                entitlements.Add(MapEntitlement(entry));
            }

            offset += page.Entitlements.Count;
            if (page.Entitlements.Count < pageLimit || offset >= page.TotalResults)
            {
                break;
            }
        }

        return entitlements;
    }
}
