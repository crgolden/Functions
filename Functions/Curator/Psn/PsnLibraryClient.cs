namespace Functions.Curator.Psn;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed partial class PsnLibraryClient : IPsnLibraryClient
{
    public const int PageSize = 200;

#pragma warning disable S1075 // fixed PSN endpoint, not environment-configurable
    internal const string EntitlementsUrl =
        "https://m.np.playstation.com/api/entitlement/v2/users/me/internal/entitlements";

    internal const string DownloadSizesUrl =
        "https://commerce.api.np.km.playstation.net/commerce/api/v1/users/me/internal_entitlements";
#pragma warning restore S1075

    internal const string DownloadSizesRequestedFields = "drm_def";
    internal const string GameContentType = "GAME";
    internal const string StartQueryKey = "start";
    internal const string SizeQueryKey = "size";
    internal const string DownloadSizesFieldsQueryKey = "fields";

    internal const string EntitlementTypes = "1,2,3,4,5";

    internal const string RequestedFields =
        "titleMeta,gameMeta,conceptMeta,rewardMeta,rewardMeta.retentionPolicy,rewardMeta.rewardMembershipType";

    internal const string EntitlementTypeQueryKey = "entitlementType";
    internal const string FieldsQueryKey = "fields";
    internal const string TitleIdQueryKey = "titleId";
    internal const string LimitQueryKey = "limit";
    internal const string OffsetQueryKey = "offset";

    private const string? AllTitleIdsRequireAValuelessTitleId = null;

    public Task<IReadOnlyList<Entitlement>> EntitlementsAsync(
        PsnSession session,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        session.RunWithReauthAsync(
            () => EntitlementsCoreAsync(session, limit, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<EntitlementDownloadSize>> DownloadSizesAsync(
        PsnSession session,
        CancellationToken cancellationToken = default) =>
        session.RunWithReauthAsync(
            () => DownloadSizesCoreAsync(session, cancellationToken), cancellationToken);

    internal static EntitlementDownloadSize? MapDownloadSize(PsnCommerceEntitlement entitlement)
    {
        var drm = entitlement.DrmDefinition;
        if (entitlement.Id is not { } entitlementId
            || drm is null
            || !string.Equals(drm.ContentType, GameContentType, StringComparison.Ordinal))
        {
            return null;
        }

        var titleMatch = TitleIdInEntitlementId().Match(entitlementId);
        if (!titleMatch.Success)
        {
            return null;
        }

        var titleId = titleMatch.Groups[1].Value;
        if (TitlePlatform.PlatformForTitleId(titleId) is not { } platform)
        {
            return null;
        }

        var bytes = drm.Contents.Sum(content => content.ContentSize ?? 0);
        return bytes > 0 ? new EntitlementDownloadSize(entitlementId, titleId, platform, bytes) : null;
    }

    [GeneratedRegex(@"^[A-Z0-9]{6}-([A-Z]{4}\d{5}_\d{2})-")]
    private static partial Regex TitleIdInEntitlementId();

    private static async Task<IReadOnlyList<EntitlementDownloadSize>> DownloadSizesCoreAsync(
        PsnSession session,
        CancellationToken cancellationToken)
    {
        var sizes = new List<EntitlementDownloadSize>();
        var start = 0;
        while (true)
        {
            var query = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [StartQueryKey] = start.ToString(CultureInfo.InvariantCulture),
                [SizeQueryKey] = PageSize.ToString(CultureInfo.InvariantCulture),
                [DownloadSizesFieldsQueryKey] = DownloadSizesRequestedFields,
            };

            using var response = await session
                .GetAsync(DownloadSizesUrl, query, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<PsnCommerceEntitlementsResponse>(body)
                ?? new PsnCommerceEntitlementsResponse();
            if (page.Entitlements.Count == 0)
            {
                break;
            }

            sizes.AddRange(page.Entitlements.Select(MapDownloadSize).OfType<EntitlementDownloadSize>());

            start += page.Entitlements.Count;
            var psnServedAShortPage = page.Entitlements.Count < PageSize;
            var startReachedPsnsReportedTotal = page.TotalResults is { } reportedTotal && start >= reportedTotal;
            if (psnServedAShortPage || startReachedPsnsReportedTotal)
            {
                break;
            }
        }

        return sizes;
    }

    private static Entitlement MapEntitlement(JsonElement entry)
    {
        var payload = entry.Deserialize<PsnEntitlementPayload>() ?? new PsnEntitlementPayload();
        var gameMetaName = payload.GameMeta?.Name;
        var titleMetaName = payload.TitleMeta?.Name;
        var titleImageUrl = AbsoluteUrl(payload.TitleMeta?.ImageUrl);
        var gameIconUrl = AbsoluteUrl(payload.GameMeta?.IconUrl);

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
            ImageUrl = titleImageUrl ?? gameIconUrl,
            TitleImageUrl = titleImageUrl,
            GameIconUrl = gameIconUrl,
            ConceptIconUrl = AbsoluteUrl(payload.ConceptMeta?.IconUrl),
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

    private static Uri? AbsoluteUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute : null;

    private static bool IsRepeatOfAnEarlierPage(
        Entitlement entitlement,
        HashSet<string> alreadyReturnedEntitlementIds) =>
        entitlement.EntitlementId is { } entitlementId
        && !string.IsNullOrWhiteSpace(entitlementId)
        && !alreadyReturnedEntitlementIds.Add(entitlementId);

    private static async Task<IReadOnlyList<Entitlement>> EntitlementsCoreAsync(
        PsnSession session,
        int? limit,
        CancellationToken cancellationToken)
    {
        var entitlements = new List<Entitlement>();
        var alreadyReturnedEntitlementIds = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;

        while (limit is null || entitlements.Count < limit.Value)
        {
            var pageLimit = limit is null ? PageSize : Math.Min(PageSize, limit.Value - entitlements.Count);
            var query = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [EntitlementTypeQueryKey] = EntitlementTypes,
                [FieldsQueryKey] = RequestedFields,
                [TitleIdQueryKey] = AllTitleIdsRequireAValuelessTitleId,
                [LimitQueryKey] = pageLimit.ToString(CultureInfo.InvariantCulture),
                [OffsetQueryKey] = offset.ToString(CultureInfo.InvariantCulture),
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
                var entitlement = MapEntitlement(entry);
                if (IsRepeatOfAnEarlierPage(entitlement, alreadyReturnedEntitlementIds))
                {
                    continue;
                }

                entitlements.Add(entitlement);
            }

            offset += page.Entitlements.Count;
            var psnServedAShortPage = page.Entitlements.Count < pageLimit;
            var offsetReachedPsnsReportedTotal = page.TotalResults is { } reportedTotal && offset >= reportedTotal;
            if (psnServedAShortPage || offsetReachedPsnsReportedTotal)
            {
                break;
            }
        }

        return entitlements;
    }
}
