namespace Functions.Curator.Psn;

using Rawg;

public static class TrophyTitleMatcher
{
    public const double DefaultMatchThreshold = 0.80;

    public static IReadOnlyDictionary<string, TrophyTitle> MatchTitles(
        IReadOnlyList<TrophyTitle> titles,
        IReadOnlyList<(string GameId, string CanonicalTitle)> games,
        double threshold = DefaultMatchThreshold)
    {
        var matched = new Dictionary<string, TrophyTitle>(StringComparer.Ordinal);
        var namedTitles = new List<TrophyTitle>();
        var normalizedTitles = new List<(int Index, string Normalized)>();
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title.Name) || title.Progress is null)
            {
                continue;
            }

            normalizedTitles.Add((namedTitles.Count, RawgMatcher.Normalize(title.Name)));
            namedTitles.Add(title);
        }

        if (games.Count == 0 || namedTitles.Count == 0)
        {
            return matched;
        }

        var candidates = new List<(double Score, string GameId, int TitleIndex)>();
        foreach (var (gameId, canonicalTitle) in games)
        {
            var normalizedGame = RawgMatcher.Normalize(canonicalTitle);
            foreach (var (index, normalizedTitle) in normalizedTitles)
            {
                var score = RawgMatcher.Similarity(normalizedGame, normalizedTitle);
                if (score >= threshold)
                {
                    candidates.Add((score, gameId, index));
                }
            }
        }

        var claimedGames = new HashSet<string>(StringComparer.Ordinal);
        var claimedTitles = new HashSet<int>();
        foreach (var (_, gameId, titleIndex) in candidates.OrderByDescending(candidate => candidate.Score))
        {
            if (claimedGames.Contains(gameId) || claimedTitles.Contains(titleIndex))
            {
                continue;
            }

            claimedGames.Add(gameId);
            claimedTitles.Add(titleIndex);
            matched[gameId] = namedTitles[titleIndex];
        }

        return matched;
    }
}
