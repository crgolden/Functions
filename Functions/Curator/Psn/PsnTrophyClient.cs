namespace Functions.Curator.Psn;

using System.Globalization;
using System.Text.Json;

public sealed class PsnTrophyClient : IPsnTrophyClient
{
    public const int TitleBatchSize = 5;

    internal const int PageSize = 50;
    internal const string LimitQueryKey = "limit";
    internal const string NpTitleIdsQueryKey = "npTitleIds";
    internal const string TrophyTitlesRoute = "users/me/trophyTitles";
    internal const string TitleTrophyTitlesRoute = "users/me/titles/trophyTitles";

    private const string MobileApiHost = "m.np.playstation.com";
    private const string TrophiesPath = "/api/trophy/v1";

    private static readonly string TrophiesUri = Uri.UriSchemeHttps + Uri.SchemeDelimiter + MobileApiHost + TrophiesPath;

    public Task<IReadOnlyList<TrophyTitle>> TrophyTitlesAsync(
        PsnSession session,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        session.RunWithReauthAsync(
            () => TrophyTitlesCoreAsync(session, limit, cancellationToken), cancellationToken);

    public Task<IReadOnlyDictionary<string, TrophyTitle>> TrophyTitlesByTitleIdAsync(
        PsnSession session,
        IReadOnlyList<string> titleIds,
        CancellationToken cancellationToken = default) =>
        session.RunWithReauthAsync(
            () => TrophyTitlesByTitleIdCoreAsync(session, titleIds, cancellationToken), cancellationToken);

    private static TrophyTitle MapTrophyTitle(PsnTrophyTitle entry) =>
        new(entry.NpCommunicationId, entry.TrophyTitleName, entry.Progress);

    private static async Task<IReadOnlyList<TrophyTitle>> TrophyTitlesCoreAsync(
        PsnSession session,
        int limit,
        CancellationToken cancellationToken)
    {
        var titles = new List<TrophyTitle>();
        var offset = 0;

        while (titles.Count < limit)
        {
            var pageLimit = Math.Min(PageSize, limit - titles.Count);
            var query = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [LimitQueryKey] = pageLimit.ToString(CultureInfo.InvariantCulture),
                ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            };

            using var response = await session.GetAsync(
                new Uri($"{TrophiesUri}/{TrophyTitlesRoute}", UriKind.Absolute), query, cancellationToken: cancellationToken);
            response.EnsureSuccessStatusCode();

            var page = JsonSerializer.Deserialize<PsnTrophyTitlesResponse>(
                await response.Content.ReadAsStringAsync(cancellationToken)) ?? new PsnTrophyTitlesResponse();
            if (page.TrophyTitles.Count == 0)
            {
                break;
            }

            titles.AddRange(page.TrophyTitles.Select(MapTrophyTitle));
            offset += page.TrophyTitles.Count;

            if (page.NextOffset is not > 0)
            {
                break;
            }
        }

        return titles;
    }

    private static async Task<IReadOnlyDictionary<string, TrophyTitle>> TrophyTitlesByTitleIdCoreAsync(
        PsnSession session,
        IReadOnlyList<string> titleIds,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NpTitleIdsQueryKey] = string.Join(",", titleIds),
        };

        using var response = await session.GetAsync(
            new Uri($"{TrophiesUri}/{TitleTrophyTitlesRoute}", UriKind.Absolute), query, cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = JsonSerializer.Deserialize<PsnTitleTrophyTitlesResponse>(
            await response.Content.ReadAsStringAsync(cancellationToken)) ?? new PsnTitleTrophyTitlesResponse();

        var byTitleId = new Dictionary<string, TrophyTitle>(StringComparer.Ordinal);
        foreach (var title in payload.Titles)
        {
            if (title.NpTitleId is not { } npTitleId)
            {
                continue;
            }

            var usable = title.TrophyTitles
                .Select(MapTrophyTitle)
                .FirstOrDefault(entry => entry.NpCommunicationId is not null && entry.Progress is not null);
            if (usable is not null)
            {
                byTitleId[npTitleId] = usable;
            }
        }

        return byTitleId;
    }
}
