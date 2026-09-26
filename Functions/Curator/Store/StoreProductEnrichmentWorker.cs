namespace Functions.Curator.Store;

using System.Diagnostics;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Microsoft.Azure.Functions.Worker;

public sealed class StoreProductEnrichmentWorker
{
    public const int DefaultBatchLimit = 500;

    public const string StoppedByContention = "concurrent_run";
    public const string StoppedByTimeBudget = "time_budget";
    public const string StoppedByRotatedQuery = "query_rotated";
    public const string StoppedByUnreachableStore = "store_unreachable";

    public static readonly TimeSpan DefaultPace = TimeSpan.FromMilliseconds(500);

    internal const string EnrichedResult = "enriched";
    internal const string UnavailableResult = "unavailable";

    private const string PassContendedEvent = "curator.store.pass-contended";
    private const string TimeBudgetEvent = "curator.store.time-budget";
    private const string QueryRotatedEvent = "curator.store.query-rotated";
    private const string StoreUnreachableEvent = "curator.store.unreachable";

    private readonly EnrichmentRepository _repository;
    private readonly IStoreGatewayClient _store;
    private readonly Telemetry _telemetry;

    public StoreProductEnrichmentWorker(EnrichmentRepository repository, IStoreGatewayClient store, Telemetry telemetry)
    {
        _repository = repository;
        _store = store;
        _telemetry = telemetry;
    }

    [Function(nameof(StoreProductEnrichmentWorker))]
    public Task Run(
        [TimerTrigger("0 30 4 * * *")] TimerInfo timer,
        CancellationToken cancellationToken) =>
        ProcessAsync(DefaultBatchLimit, DefaultPace, new JobTimeBudget(), cancellationToken);

    public async Task<StoreProductPassOutcome> ProcessAsync(
        int limit,
        TimeSpan pace,
        JobTimeBudget timeBudget,
        CancellationToken cancellationToken = default)
    {
        await using var pass = await _repository.TryLockStoreProductPassAsync(cancellationToken).ConfigureAwait(false);
        if (!pass.Acquired)
        {
            Telemetry.Tracing.RecordHandledFailure(PassContendedEvent, "Another storefront pass holds the lock.");
            return new StoreProductPassOutcome(0, 0, 0, StoppedByContention);
        }

        var candidates = await _repository
            .GetStoreProductsNeedingPsnEnrichmentAsync(limit, cancellationToken)
            .ConfigureAwait(false);
        var genres = await _repository.GetActiveGenresWithLabelsAsync(cancellationToken).ConfigureAwait(false);

        var enriched = 0;
        var unavailable = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (timeBudget.Expired)
            {
                Telemetry.Tracing.RecordHandledFailure(TimeBudgetEvent, $"{candidates.Count - index} products left for the next pass.");
                return new StoreProductPassOutcome(enriched, unavailable, candidates.Count - index, StoppedByTimeBudget);
            }

            var candidate = candidates[index];
            var askedAt = Stopwatch.GetTimestamp();
            StoreProductNode? product;
            StoreStarRating? starRating;
            try
            {
                product = await _store.ProductAsync(candidate.StoreProductId, cancellationToken).ConfigureAwait(false);
                starRating = product is null
                    ? null
                    : await _store.StarRatingAsync(candidate.StoreProductId, cancellationToken).ConfigureAwait(false);
            }
            catch (StoreQueryRotatedException exception)
            {
                Telemetry.Tracing.RecordHandledException(QueryRotatedEvent, exception);
                return new StoreProductPassOutcome(enriched, unavailable, candidates.Count - index, StoppedByRotatedQuery);
            }
            catch (HttpRequestException exception)
            {
                Telemetry.Tracing.RecordHandledException(StoreUnreachableEvent, exception);
                return new StoreProductPassOutcome(enriched, unavailable, candidates.Count - index, StoppedByUnreachableStore);
            }

            await SaveAsync(candidate, product, starRating, genres, cancellationToken).ConfigureAwait(false);
            if (product is null)
            {
                unavailable++;
                _telemetry.StoreProductsProcessed(1, UnavailableResult);
            }
            else
            {
                enriched++;
                _telemetry.StoreProductsProcessed(1, EnrichedResult);
            }

            await PaceAsync(pace - Stopwatch.GetElapsedTime(askedAt), index == candidates.Count - 1, cancellationToken).ConfigureAwait(false);
        }

        return new StoreProductPassOutcome(enriched, unavailable, 0, null);
    }

    private static Task PaceAsync(TimeSpan pace, bool isLastCandidate, CancellationToken cancellationToken) =>
        pace > TimeSpan.Zero && !isLastCandidate ? Task.Delay(pace, cancellationToken) : Task.CompletedTask;

    private async Task SaveAsync(
        StoreProductCandidate candidate,
        StoreProductNode? product,
        StoreStarRating? starRating,
        IReadOnlyList<StoreGenre> genres,
        CancellationToken cancellationToken)
    {
        var genreKeys = product is null ? [] : StoreProductSignals.GenreKeys(product, genres);
        var (genreId, subgenreId) = StoreProductSignals.PickGenres(genreKeys, genres);
        await _repository
            .SaveGameEnrichmentAsync(
                candidate.GameId, genreId, subgenreId, StoreProductSignals.Build(product, starRating), cancellationToken)
            .ConfigureAwait(false);
        if (product is not null)
        {
            await _repository
                .SavePsnCatalogCacheAsync(
                    StoreProductSignals.CacheEntry(candidate.TitleId, product, starRating, genreKeys), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
