namespace Functions.Curator.Library;

using Functions.Curator.Catalog;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.Psn;

public sealed class LibraryBuildOrchestrator
{
    internal const string DownloadSizesUnavailableEvent = "curator.library.download-sizes-unavailable";

    private readonly IngestionService _ingestionService;
    private readonly CatalogRepository _catalogRepository;
    private readonly LibraryRepository _libraryRepository;
    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly EnrichmentBatchProcessor _batchProcessor;
    private readonly TrophyMatchService _trophyMatchService;

    public LibraryBuildOrchestrator(
        IngestionService ingestionService,
        CatalogRepository catalogRepository,
        LibraryRepository libraryRepository,
        EnrichmentRepository enrichmentRepository,
        EnrichmentBatchProcessor batchProcessor,
        TrophyMatchService trophyMatchService)
    {
        _ingestionService = ingestionService;
        _catalogRepository = catalogRepository;
        _libraryRepository = libraryRepository;
        _enrichmentRepository = enrichmentRepository;
        _batchProcessor = batchProcessor;
        _trophyMatchService = trophyMatchService;
    }

    public async Task<IReadOnlyList<CanonicalGame>> CanonicalizeAsync(
        Guid identitySub,
        PsnSession session,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var (_, snapshots) = await _ingestionService
            .IngestAsync(identitySub, session, limit, cancellationToken)
            .ConfigureAwait(false);

        var franchiseRules = await _catalogRepository.ListFranchiseRulesAsync(cancellationToken).ConfigureAwait(false);
        var editionRanks = await _catalogRepository.GetEditionRanksAsync(cancellationToken).ConfigureAwait(false);
        var nameOverrides = await _catalogRepository.GetNameOverridesAsync(cancellationToken).ConfigureAwait(false);
        var globallyExcluded = await _catalogRepository
            .GetGloballyExcludedConceptIdsAsync(cancellationToken)
            .ConfigureAwait(false);

        return CanonicalizationService.Canonicalize(
            snapshots, franchiseRules, editionRanks, nameOverrides, globallyExcluded);
    }

    public async Task<List<Guid>> PersistAndLinkAsync(
        Guid identitySub,
        IReadOnlyList<CanonicalGame> canonicalGames,
        CancellationToken cancellationToken = default)
    {
        var gameIds = new List<Guid>(canonicalGames.Count);
        var entries = new List<LibraryEntryRow>(canonicalGames.Count);
        foreach (var game in canonicalGames)
        {
            var gameId = await _catalogRepository.UpsertGameAsync(game, cancellationToken).ConfigureAwait(false);
            entries.Add(LibraryEntryRow.ForCanonicalGame(gameId, game));
            gameIds.Add(gameId);
        }

        await _libraryRepository.UpsertEntriesAsync(identitySub, entries, cancellationToken).ConfigureAwait(false);
        return gameIds;
    }

    public async Task<int> RecordDownloadSizesAsync(
        Guid identitySub,
        PsnSession session,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<EntitlementDownloadSize> sizes;
        try
        {
            sizes = await _ingestionService.DownloadSizesAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDownloadSizeLookupFailure(exception, cancellationToken))
        {
            Telemetry.Tracing.RecordHandledException(DownloadSizesUnavailableEvent, exception);
            return 0;
        }

        return await _libraryRepository
            .UpsertDownloadSizesAsync(identitySub, sizes, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EnrichmentBatchResult> EnrichDeltaAsync(
        EnrichmentContext enrichment,
        IReadOnlyList<CanonicalGame> canonicalGames,
        IReadOnlyList<Guid> gameIds,
        IReadOnlyList<PublisherTierRule> publisherTierRules,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        if (canonicalGames.Count != gameIds.Count)
        {
            throw new ArgumentException(
                "canonicalGames and gameIds must line up one-to-one.", nameof(gameIds));
        }

        var needs = (await _enrichmentRepository
                .GetEnrichmentNeedsAsync(gameIds, cancellationToken)
                .ConfigureAwait(false))
            .DistinctBy(need => need.GameId, EqualityComparer<Guid>.Default)
            .ToDictionary(need => need.GameId, EqualityComparer<Guid>.Default);

        var candidates = gameIds
            .Select((gameId, index) => (GameId: gameId, Game: canonicalGames[index]))
            .Where(pair => needs.ContainsKey(pair.GameId))
            .Select(pair => new EnrichmentCandidate(
                pair.GameId,
                pair.Game.CanonicalTitle,
                pair.Game.ProductId,
                pair.Game.WinningTitleId,
                pair.Game.NativePs5,
                needs[pair.GameId]))
            .ToList();

        return await _batchProcessor
            .EnrichGamesAsync(
                enrichment,
                candidates,
                publisherTierRules,
                timeBudget: timeBudget,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<TrophyMatchResult> MatchTrophiesAsync(
        Guid identitySub,
        IReadOnlyList<CanonicalGame> canonicalGames,
        IReadOnlyList<Guid> gameIds,
        PsnSession? trophySession,
        CancellationToken cancellationToken = default) =>
        _trophyMatchService.MatchTrophiesAsync(
            trophySession,
            identitySub,
            canonicalGames,
            gameIds,
            cancellationToken);

    private static bool IsDownloadSizeLookupFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or PsnAuthException or System.Text.Json.JsonException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}
