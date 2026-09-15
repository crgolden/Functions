namespace Functions.Curator.Library;

using Catalog;
using Psn;

public static class TrophyMatchService
{
    public const string ExactMatchMethod = "exact";

    public const string FuzzyMatchMethod = "fuzzy";

    public const int TrophyTitlesLimit = 500;

    public static async Task<TrophyMatchResult> MatchTrophiesAsync(
        LibraryRepository libraryRepository,
        IPsnTrophyClient trophyClient,
        PsnSession? session,
        string identitySub,
        IReadOnlyList<CanonicalGame> canonicalGames,
        IReadOnlyList<string> gameIds,
        CancellationToken cancellationToken = default)
    {
        if (canonicalGames.Count != gameIds.Count)
        {
            throw new ArgumentException(
                "canonicalGames and gameIds must line up one-to-one.", nameof(gameIds));
        }

        if (session is null)
        {
            return new TrophyMatchResult(0, 0, 0);
        }

        var unmatched = (await libraryRepository
                .GetUnmatchedGameIdsAsync(identitySub, gameIds, cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = gameIds
            .Select((gameId, index) => (GameId: gameId, Game: canonicalGames[index]))
            .Where(candidate => unmatched.Contains(candidate.GameId))
            .ToList();

        var (exactMatchable, stillUnmatched) = SplitByExactLookup(candidates);
        var exactMatchedCount = await MatchExactAsync(
                libraryRepository, trophyClient, session, identitySub, exactMatchable, stillUnmatched, cancellationToken)
            .ConfigureAwait(false);
        var (fuzzyMatchedCount, titles) = await MatchFuzzyAsync(
                libraryRepository, trophyClient, session, identitySub, stillUnmatched, cancellationToken)
            .ConfigureAwait(false);

        if (titles.Count == 0)
        {
            titles = await trophyClient.TrophyTitlesAsync(session, TrophyTitlesLimit, cancellationToken).ConfigureAwait(false);
        }

        var progressUpdatedCount = await libraryRepository
            .RefreshTrophyProgressAsync(identitySub, ProgressByNpCommunicationId(titles), cancellationToken)
            .ConfigureAwait(false);

        return new TrophyMatchResult(
            exactMatchedCount, fuzzyMatchedCount, candidates.Count, progressUpdatedCount);
    }

    private static (List<(string GameId, string CanonicalTitle, string TitleId)> ExactMatchable, List<(string GameId, string CanonicalTitle)> StillUnmatched)
        SplitByExactLookup(List<(string GameId, CanonicalGame Game)> candidates)
    {
        var exactMatchable = new List<(string GameId, string CanonicalTitle, string TitleId)>();
        var stillUnmatched = new List<(string GameId, string CanonicalTitle)>();
        foreach (var (gameId, game) in candidates)
        {
            if (game.WinningTitleId is { } titleId
                && string.Equals(TitlePlatform.PlatformForTitleId(titleId), TitlePlatform.Ps4, StringComparison.Ordinal))
            {
                exactMatchable.Add((gameId, game.CanonicalTitle, titleId));
            }
            else
            {
                stillUnmatched.Add((gameId, game.CanonicalTitle));
            }
        }

        return (exactMatchable, stillUnmatched);
    }

    private static async Task<int> MatchExactAsync(
        LibraryRepository libraryRepository,
        IPsnTrophyClient trophyClient,
        PsnSession session,
        string identitySub,
        List<(string GameId, string CanonicalTitle, string TitleId)> exactMatchable,
        List<(string GameId, string CanonicalTitle)> stillUnmatched,
        CancellationToken cancellationToken)
    {
        var exactMatchedCount = 0;
        foreach (var batch in exactMatchable.Chunk(PsnTrophyClient.TitleBatchSize))
        {
            var found = await trophyClient
                .TrophyTitlesByTitleIdAsync(
                    session, [.. batch.Select(entry => entry.TitleId)], cancellationToken)
                .ConfigureAwait(false);

            foreach (var (gameId, canonicalTitle, titleId) in batch)
            {
                if (!found.TryGetValue(titleId, out var exact))
                {
                    stillUnmatched.Add((gameId, canonicalTitle));
                    continue;
                }

                await libraryRepository
                    .SetTrophyMatchAsync(
                        identitySub, gameId, exact.NpCommunicationId, ExactMatchMethod, exact.Progress, cancellationToken)
                    .ConfigureAwait(false);
                exactMatchedCount++;
            }
        }

        return exactMatchedCount;
    }

    private static async Task<(int FuzzyMatchedCount, IReadOnlyList<TrophyTitle> Titles)> MatchFuzzyAsync(
        LibraryRepository libraryRepository,
        IPsnTrophyClient trophyClient,
        PsnSession session,
        string identitySub,
        List<(string GameId, string CanonicalTitle)> stillUnmatched,
        CancellationToken cancellationToken)
    {
        if (stillUnmatched.Count == 0)
        {
            IReadOnlyList<TrophyTitle> noTitles = [];
            return (0, noTitles);
        }

        var titles = await trophyClient.TrophyTitlesAsync(session, TrophyTitlesLimit, cancellationToken).ConfigureAwait(false);
        var fuzzyMatches = TrophyTitleMatcher.MatchTitles(titles, stillUnmatched);
        var fuzzyMatchedCount = 0;
        foreach (var (gameId, _) in stillUnmatched)
        {
            var matched = fuzzyMatches.GetValueOrDefault(gameId);
            if (matched?.NpCommunicationId is { } npCommunicationId)
            {
                await libraryRepository
                    .SetTrophyMatchAsync(identitySub, gameId, npCommunicationId, FuzzyMatchMethod, matched.Progress, cancellationToken)
                    .ConfigureAwait(false);
                fuzzyMatchedCount++;
            }
            else
            {
                await libraryRepository
                    .SetTrophyMatchAsync(identitySub, gameId, npCommunicationId: null, method: null, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return (fuzzyMatchedCount, titles);
    }

    private static Dictionary<string, int> ProgressByNpCommunicationId(IReadOnlyList<TrophyTitle> titles)
    {
        var progressByNpId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            if (title.NpCommunicationId is { } npCommunicationId && title.Progress is { } progress)
            {
                progressByNpId[npCommunicationId] = progress;
            }
        }

        return progressByNpId;
    }
}
