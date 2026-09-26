namespace Functions.Curator.Library;

using Functions.Curator.Catalog;
using Functions.Curator.Psn;

public sealed class TrophyMatchService
{
    public const string ExactMatchMethod = "exact";

    public const string FuzzyMatchMethod = "fuzzy";

    public const int TrophyTitlesLimit = 500;

    private readonly LibraryRepository _libraryRepository;
    private readonly IPsnTrophyClient _trophyClient;

    public TrophyMatchService(LibraryRepository libraryRepository, IPsnTrophyClient trophyClient)
    {
        _libraryRepository = libraryRepository;
        _trophyClient = trophyClient;
    }

    public async Task<TrophyMatchResult> MatchTrophiesAsync(
        PsnSession? session,
        Guid identitySub,
        IReadOnlyList<CanonicalGame> canonicalGames,
        IReadOnlyList<Guid> gameIds,
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

        var unmatched = (await _libraryRepository
                .GetUnmatchedGameIdsAsync(identitySub, gameIds, cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(EqualityComparer<Guid>.Default);

        var candidates = gameIds
            .Select((gameId, index) => (GameId: gameId, Game: canonicalGames[index]))
            .Where(candidate => unmatched.Contains(candidate.GameId))
            .ToList();

        var (exactMatchable, stillUnmatched) = SplitByExactLookup(candidates);
        var exactMatchedCount = await MatchExactAsync(
                session, identitySub, exactMatchable, stillUnmatched, cancellationToken)
            .ConfigureAwait(false);
        var (fuzzyMatchedCount, titles) = await MatchFuzzyAsync(
                session, identitySub, stillUnmatched, cancellationToken)
            .ConfigureAwait(false);

        if (titles.Count == 0)
        {
            titles = await _trophyClient.TrophyTitlesAsync(session, TrophyTitlesLimit, cancellationToken).ConfigureAwait(false);
        }

        var progressUpdatedCount = await _libraryRepository
            .RefreshTrophyProgressAsync(identitySub, ProgressByNpCommunicationId(titles), cancellationToken)
            .ConfigureAwait(false);

        return new TrophyMatchResult(
            exactMatchedCount, fuzzyMatchedCount, candidates.Count, progressUpdatedCount);
    }

    private static (List<(Guid GameId, string CanonicalTitle, string TitleId)> ExactMatchable, List<(Guid GameId, string CanonicalTitle)> StillUnmatched)
        SplitByExactLookup(List<(Guid GameId, CanonicalGame Game)> candidates)
    {
        var exactMatchable = new List<(Guid GameId, string CanonicalTitle, string TitleId)>();
        var stillUnmatched = new List<(Guid GameId, string CanonicalTitle)>();
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

    private async Task<int> MatchExactAsync(
        PsnSession session,
        Guid identitySub,
        List<(Guid GameId, string CanonicalTitle, string TitleId)> exactMatchable,
        List<(Guid GameId, string CanonicalTitle)> stillUnmatched,
        CancellationToken cancellationToken)
    {
        var exactMatchedCount = 0;
        foreach (var batch in exactMatchable.Chunk(PsnTrophyClient.TitleBatchSize))
        {
            var found = await _trophyClient
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

                await _libraryRepository
                    .SetTrophyMatchAsync(
                        identitySub, gameId, exact.NpCommunicationId, ExactMatchMethod, exact.Progress, cancellationToken)
                    .ConfigureAwait(false);
                exactMatchedCount++;
            }
        }

        return exactMatchedCount;
    }

    private async Task<(int FuzzyMatchedCount, IReadOnlyList<TrophyTitle> Titles)> MatchFuzzyAsync(
        PsnSession session,
        Guid identitySub,
        List<(Guid GameId, string CanonicalTitle)> stillUnmatched,
        CancellationToken cancellationToken)
    {
        if (stillUnmatched.Count == 0)
        {
            IReadOnlyList<TrophyTitle> noTitles = [];
            return (0, noTitles);
        }

        var titles = await _trophyClient.TrophyTitlesAsync(session, TrophyTitlesLimit, cancellationToken).ConfigureAwait(false);
        var fuzzyMatches = TrophyTitleMatcher.MatchTitles(titles, stillUnmatched);
        var fuzzyMatchedCount = 0;
        foreach (var (gameId, _) in stillUnmatched)
        {
            var matched = fuzzyMatches.GetValueOrDefault(gameId);
            if (matched?.NpCommunicationId is { } npCommunicationId)
            {
                await _libraryRepository
                    .SetTrophyMatchAsync(identitySub, gameId, npCommunicationId, FuzzyMatchMethod, matched.Progress, cancellationToken)
                    .ConfigureAwait(false);
                fuzzyMatchedCount++;
            }
            else
            {
                await _libraryRepository
                    .SetTrophyMatchAsync(identitySub, gameId, npCommunicationId: null, method: null, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return (fuzzyMatchedCount, titles);
    }
}
