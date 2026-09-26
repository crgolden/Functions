namespace Functions.Curator.Store;

using System.Diagnostics;
using Functions.Curator.Jobs;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

public sealed class StoreCatalogCrawlWorker
{
    public const int PageSize = 100;

    public const string StoppedByContention = "concurrent_run";
    public const string StoppedByTimeBudget = "time_budget";
    public const string StoppedByRotatedQuery = "query_rotated";
    public const string StoppedByUnreachableStore = "store_unreachable";
    public const string StoppedByPageBudget = "page_budget";
    public const string StoppedByRenamedCategory = "category_renamed";
    public const string StoppedByNoProducts = "no_products";

    private const string CrawlContendedEvent = "curator.store.crawl-contended";
    private const string TimeBudgetEvent = "curator.store.crawl-time-budget";
    private const string QueryRotatedEvent = "curator.store.crawl-query-rotated";
    private const string StoreUnreachableEvent = "curator.store.crawl-unreachable";
    private const string CategoryRenamedEvent = "curator.store.crawl-category-renamed";
    private const string NoProductsEvent = "curator.store.crawl-no-products";

    private readonly StoreCatalogCrawlRepository _repository;
    private readonly IStoreGatewayClient _store;
    private readonly int _pagesPerRun;
    private readonly TimeSpan _pace;
    private readonly int _rewalkDays;

    public StoreCatalogCrawlWorker(
        StoreCatalogCrawlRepository repository,
        IStoreGatewayClient store,
        IConfiguration configuration)
    {
        _repository = repository;
        _store = store;
        _pagesPerRun = configuration.GetRequired<int>(CuratorConfigurationKeys.StoreCrawlPagesPerRun);
        _pace = TimeSpan.FromMilliseconds(
            configuration.GetRequired<int>(CuratorConfigurationKeys.StoreCrawlPaceMilliseconds));
        _rewalkDays = configuration.GetRequired<int>(CuratorConfigurationKeys.StoreCrawlRewalkDays);
    }

    [Function(nameof(StoreCatalogCrawlWorker))]
    public Task Run(
        [TimerTrigger("0 0 */3 * * *")] TimerInfo timer,
        CancellationToken cancellationToken) =>
        ProcessAsync(_pagesPerRun, _pace, new JobTimeBudget(), cancellationToken);

    public async Task<StoreCatalogCrawlOutcome> ProcessAsync(
        int pageBudget,
        TimeSpan pace,
        JobTimeBudget timeBudget,
        CancellationToken cancellationToken = default)
    {
        await using var crawl = await _repository.TryLockCrawlAsync(cancellationToken).ConfigureAwait(false);
        if (!crawl.Acquired)
        {
            Telemetry.Tracing.RecordHandledFailure(CrawlContendedEvent, "Another crawl holds the lock.");
            return new StoreCatalogCrawlOutcome(0, 0, 0, StoppedByContention);
        }

        var categories = await _repository.GetCategoriesAsync(cancellationToken).ConfigureAwait(false);
        var tally = new CrawlTally();

        foreach (var category in categories)
        {
            if (tally.PagesRead >= pageBudget)
            {
                tally.Stop(StoppedByPageBudget);
                break;
            }

            if (timeBudget.Expired)
            {
                Telemetry.Tracing.RecordHandledFailure(TimeBudgetEvent, "The crawl stopped on its time budget.");
                tally.Stop(StoppedByTimeBudget);
                break;
            }

            await WalkCategoryAsync(category, pageBudget, pace, timeBudget, tally, cancellationToken)
                .ConfigureAwait(false);
            if (tally.EndsTheRun)
            {
                break;
            }
        }

        return tally.ToOutcome();
    }

    internal static int NextOffset(StoreCategoryPageInfo? pageInfo, int requestedOffset, int productCount)
    {
        var reported = pageInfo?.Offset ?? requestedOffset;
        var basis = reported >= requestedOffset ? reported : requestedOffset;
        return basis + Math.Max(productCount, 1);
    }

    internal static bool NameMatches(string? reportingName, string prefix) =>
        reportingName is not null && reportingName.StartsWith(prefix, StringComparison.Ordinal);

    private static Task PaceAsync(TimeSpan pace, CancellationToken cancellationToken) =>
        pace > TimeSpan.Zero ? Task.Delay(pace, cancellationToken) : Task.CompletedTask;

    private async Task WalkCategoryAsync(
        StoreCrawlCategory category,
        int pageBudget,
        TimeSpan pace,
        JobTimeBudget timeBudget,
        CrawlTally tally,
        CancellationToken cancellationToken)
    {
        var offset = await StartingOffsetAsync(category, cancellationToken).ConfigureAwait(false);
        var startingOffset = offset;
        var pagesInCategory = 0;

        while (tally.PagesRead < pageBudget && !timeBudget.Expired)
        {
            var askedAt = Stopwatch.GetTimestamp();
            var page = await ReadPageAsync(category, offset, tally, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                return;
            }

            if (pagesInCategory == 0 && !NameMatches(page.ReportingName, category.ReportingNamePrefix))
            {
                Telemetry.Tracing.RecordHandledFailure(
                    CategoryRenamedEvent,
                    $"The {category.Platform} category {category.CategoryId} answers to {page.ReportingName}, not {category.ReportingNamePrefix}.");
                tally.EndRun(StoppedByRenamedCategory);
                return;
            }

            tally.CountPage(page.Products.Count);
            pagesInCategory++;
            if (page.Products.Count == 0 && pagesInCategory == 1 && startingOffset == 0)
            {
                Telemetry.Tracing.RecordHandledFailure(
                    NoProductsEvent,
                    $"The {category.Platform} category {category.CategoryId} reports {page.PageInfo?.TotalCount ?? 0} products and returns none, so it is not a walkable category.");
                tally.Stop(StoppedByNoProducts);
                return;
            }

            tally.CountGames(await AdmitFullGamesAsync(page, cancellationToken).ConfigureAwait(false));

            offset = NextOffset(page.PageInfo, offset, page.Products.Count);
            var isLast = page.PageInfo?.IsLast == true || page.Products.Count == 0;
            await _repository
                .SaveProgressAsync(
                    category.CategoryId, offset, page.PageInfo?.TotalCount ?? 0, isLast, cancellationToken)
                .ConfigureAwait(false);
            if (isLast)
            {
                return;
            }

            await PaceAsync(pace - Stopwatch.GetElapsedTime(askedAt), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StoreCategoryGrid?> ReadPageAsync(
        StoreCrawlCategory category,
        int offset,
        CrawlTally tally,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store
                .CategoryPageAsync(category.CategoryId.ToString(), offset, PageSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreQueryRotatedException exception)
        {
            Telemetry.Tracing.RecordHandledException(QueryRotatedEvent, exception);
            tally.EndRun(StoppedByRotatedQuery);
            return null;
        }
        catch (HttpRequestException exception)
        {
            Telemetry.Tracing.RecordHandledException(StoreUnreachableEvent, exception);
            tally.EndRun(StoppedByUnreachableStore);
            return null;
        }
    }

    private async Task<int> AdmitFullGamesAsync(StoreCategoryGrid page, CancellationToken cancellationToken)
    {
        var fullGames = page.Products.Where(product => product.IsFullGame).ToArray();
        return fullGames.Length == 0
            ? 0
            : await _repository.AdmitAsync(fullGames, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> StartingOffsetAsync(StoreCrawlCategory category, CancellationToken cancellationToken)
    {
        if (category.WalkCompletedAt is not { } completedAt)
        {
            return category.NextOffset;
        }

        if (DateTimeOffset.UtcNow - completedAt < TimeSpan.FromDays(_rewalkDays))
        {
            return category.NextOffset;
        }

        await _repository.RestartWalkAsync(category.CategoryId, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private sealed class CrawlTally
    {
        public int PagesRead { get; private set; }

        public int ProductsSeen { get; private set; }

        public int GamesCreated { get; private set; }

        public string? StoppedReason { get; private set; }

        public bool EndsTheRun { get; private set; }

        public void CountPage(int productCount)
        {
            PagesRead++;
            ProductsSeen += productCount;
        }

        public void CountGames(int created) => GamesCreated += created;

        public void Stop(string reason) => StoppedReason ??= reason;

        public void EndRun(string reason)
        {
            StoppedReason = reason;
            EndsTheRun = true;
        }

        public StoreCatalogCrawlOutcome ToOutcome() =>
            new(PagesRead, ProductsSeen, GamesCreated, StoppedReason);
    }
}
