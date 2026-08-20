namespace Functions.Curator.Psn;

using System.Globalization;
using System.Text.Json;

public sealed class PsnTrophyClient : IPsnTrophyClient
{
    public const int TitleBatchSize = 5;

#pragma warning disable S1075 // fixed PSN endpoint, not environment-configurable
    private const string TrophiesUri = "https://m.np.playstation.com/api/trophy/v1";
#pragma warning restore S1075

    private const int PageSize = 50;

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
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["limit"] = pageLimit.ToString(CultureInfo.InvariantCulture),
                ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            };

            using var response = await session.GetAsync(
                $"{TrophiesUri}/users/me/trophyTitles", query, cancellationToken: cancellationToken);
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
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["npTitleIds"] = string.Join(",", titleIds),
        };

        using var response = await session.GetAsync(
            $"{TrophiesUri}/users/me/titles/trophyTitles", query, cancellationToken: cancellationToken);
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
