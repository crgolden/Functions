namespace Functions.Curator.Library;

using Catalog;
using Enrichment;
using Jobs;
using Psn;

public sealed class LibraryBuildOrchestrator
{
    private readonly IngestionService _ingestionService;
    private readonly CatalogRepository _catalogRepository;
    private readonly LibraryRepository _libraryRepository;
    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly EnrichmentOrchestrationService _enrichmentService;

    public LibraryBuildOrchestrator(
        IngestionService ingestionService,
        CatalogRepository catalogRepository,
        LibraryRepository libraryRepository,
        EnrichmentRepository enrichmentRepository,
        EnrichmentOrchestrationService enrichmentService)
    {
        _ingestionService = ingestionService;
        _catalogRepository = catalogRepository;
        _libraryRepository = libraryRepository;
        _enrichmentRepository = enrichmentRepository;
        _enrichmentService = enrichmentService;
    }

    public async Task<IReadOnlyList<CanonicalGame>> CanonicalizeAsync(
        string identitySub,
        PsnSession session,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var (_, snapshots) = await _ingestionService
            .IngestAsync(identitySub, session, limit, cancellationToken)
            .ConfigureAwait(false);

        var exclusionRules = await _catalogRepository.ListExclusionRulesAsync(cancellationToken).ConfigureAwait(false);
        var franchiseRules = await _catalogRepository.ListFranchiseRulesAsync(cancellationToken).ConfigureAwait(false);
        var editionRanks = await _catalogRepository.GetEditionRanksAsync(cancellationToken).ConfigureAwait(false);
        var nameOverrides = await _catalogRepository.GetNameOverridesAsync(cancellationToken).ConfigureAwait(false);
        var globallyExcluded = await _catalogRepository
            .GetGloballyExcludedConceptIdsAsync(cancellationToken)
            .ConfigureAwait(false);

        return CanonicalizationService.Canonicalize(
            snapshots, exclusionRules, franchiseRules, editionRanks, nameOverrides, globallyExcluded);
    }

    public async Task<List<string>> PersistAndLinkAsync(
        string identitySub,
        IReadOnlyList<CanonicalGame> canonicalGames,
        CancellationToken cancellationToken = default)
    {
        var gameIds = new List<string>(canonicalGames.Count);
        var entries = new List<LibraryEntryRow>(canonicalGames.Count);
        foreach (var game in canonicalGames)
        {
            var gameId = await _catalogRepository.UpsertGameAsync(game, cancellationToken).ConfigureAwait(false);
            entries.Add(LibraryEntryRow.Create(
                gameId,
                game.NativePs5,
                game.Ps4Eligible,
                game.CanonicalTitle,
                game.WinningEntitlementId,
                game.ProductId,
                game.WinningTitleId,
                game.Platforms,
                game.Active));
            gameIds.Add(gameId);
        }

        await _libraryRepository.UpsertEntriesAsync(identitySub, entries, cancellationToken).ConfigureAwait(false);
        return gameIds;
    }

    public async Task<EnrichmentBatchResult> EnrichDeltaAsync(
        IReadOnlyList<CanonicalGame> canonicalGames,
        IReadOnlyList<string> gameIds,
        IReadOnlyList<PublisherTierRule> publisherTierRules,
        EnrichmentCredentials credentials,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        if (canonicalGames.Count != gameIds.Count)
        {
            throw new ArgumentException(
                "canonicalGames and gameIds must line up one-to-one.", nameof(gameIds));
        }

        var unenriched = (await _enrichmentRepository
                .GetUnenrichedGameIdsAsync(gameIds, cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = gameIds
            .Select((gameId, index) => (GameId: gameId, Game: canonicalGames[index]))
            .Where(pair => unenriched.Contains(pair.GameId))
            .Select(pair => new EnrichmentCandidate(
                pair.GameId,
                pair.Game.CanonicalTitle,
                pair.Game.ProductId,
                pair.Game.WinningTitleId,
                pair.Game.NativePs5))
            .ToList();

        return await EnrichmentBatchProcessor
            .EnrichGamesAsync(
                _enrichmentService,
                _enrichmentRepository,
                candidates,
                publisherTierRules,
                credentials,
                timeBudget: timeBudget,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<TrophyMatchResult> MatchTrophiesAsync(
        string identitySub,
        IReadOnlyList<CanonicalGame> canonicalGames,
        IReadOnlyList<string> gameIds,
        IPsnTrophyClient trophyClient,
        PsnSession? trophySession,
        CancellationToken cancellationToken = default) =>
        TrophyMatchService.MatchTrophiesAsync(
            _libraryRepository,
            trophyClient,
            trophySession,
            identitySub,
            canonicalGames,
            gameIds,
            cancellationToken);
}
