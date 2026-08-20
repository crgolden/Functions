namespace Functions.Curator.Psn;

using System.Globalization;
using System.Text.Json;

public sealed class PsnCatalogClient : ICatalogClient
{
    internal const string NoOfPlayersNoticeType = "NO_OF_PLAYERS";
    internal const string NoOfNetworkPlayersNoticeType = "NO_OF_NETWORK_PLAYERS";
    internal const string NoOfNetworkPlayersPsPlusNoticeType = "NO_OF_NETWORK_PLAYERS_PS_PLUS";
    internal const string AgeQueryValue = "99";
    internal const string CountryQueryValue = "US";
    internal const string LanguageQueryValue = "en-US";

    internal static readonly IReadOnlyList<string> CoverImagePreference = ["GAMEHUB_COVER_ART", "MASTER", "LOGO"];

#pragma warning disable S1075 // fixed PSN endpoint, not environment-configurable
    private const string GameTitlesUri = "https://m.np.playstation.com/api/catalog/v2/titles";
#pragma warning restore S1075

    private static readonly HashSet<string> PlayerCountNoticeTypes = new(StringComparer.Ordinal)
    {
        NoOfPlayersNoticeType,
        NoOfNetworkPlayersNoticeType,
        NoOfNetworkPlayersPsPlusNoticeType,
    };

    public Task<TitleConcept> TitleConceptAsync(
        PsnSession session,
        string titleId,
        CancellationToken cancellationToken = default) =>
        session.RunWithReauthAsync(
            () => TitleConceptCoreAsync(session, titleId, cancellationToken), cancellationToken);

    private static TitleConcept MapConcept(PsnConceptPayload concept) => new()
    {
        ConceptId = concept.Id?.ToString(CultureInfo.InvariantCulture),
        Name = concept.Name,
        Type = concept.Type,
        Publisher = concept.PublisherName,
        ReleaseDate = concept.ReleaseDate?.Date?.ToUniversalTime(),
        MinimumAge = concept.MinimumAge,
        ContentRating = concept.ContentRating?.Description,
        RatingAuthority = concept.ContentRating?.Authority,
        StarRating = concept.StarRating?.Score,
        Genres = concept.Genres,
        TitleIds = concept.TitleIds,
        CoverImageUrl = CoverImageUrl(concept.Media),
        Multiplayer = Multiplayer(concept.CompatibilityNotices),
    };

    private static string? CoverImageUrl(PsnConceptMedia? media)
    {
        var byType = new Dictionary<string, string>(StringComparer.Ordinal);
        string? firstUrl = null;

        foreach (var image in media?.Images ?? [])
        {
            var url = image.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            firstUrl ??= url;
            var type = image.Type;
            if (!string.IsNullOrWhiteSpace(type))
            {
                byType[type] = url;
            }
        }

        foreach (var preferred in CoverImagePreference)
        {
            if (byType.TryGetValue(preferred, out var preferredUrl))
            {
                return preferredUrl;
            }
        }

        return firstUrl;
    }

    private static bool? Multiplayer(IReadOnlyList<PsnCompatibilityNotice> notices)
    {
        var counts = notices
            .Where(notice => notice.Type is { } type && PlayerCountNoticeTypes.Contains(type))
            .Select(notice => PlayerCount(notice.Value))
            .OfType<int>()
            .ToList();

        return counts.Count == 0 ? null : counts.Max() > 1;
    }

    private static int? PlayerCount(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt32(out var number) ? number : null,
        JsonValueKind.String => int.TryParse(
            value.GetString(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed) ? parsed : null,
        _ => null,
    };

    private static async Task<TitleConcept> TitleConceptCoreAsync(
        PsnSession session,
        string titleId,
        CancellationToken cancellationToken)
    {
        using var response = await session.GetAsync(
            $"{GameTitlesUri}/{titleId}/concepts",
            new Dictionary<string, string>
            {
                ["age"] = AgeQueryValue,
                ["country"] = CountryQueryValue,
                ["language"] = LanguageQueryValue,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var concepts = JsonSerializer.Deserialize<IReadOnlyList<PsnConceptPayload>>(body) ?? [];

        return concepts.Count == 0 ? new TitleConcept() : MapConcept(concepts[0]);
    }
}
