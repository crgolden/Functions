namespace Functions.Tests.Unit;

using System.Globalization;
using Functions.Curator;
using Functions.Curator.Jobs;
using Functions.Curator.Psn;
using Functions.Curator.Store;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Extensions.Configuration;

[Trait("Category", "Unit")]
public sealed class StoreCatalogCrawlWorkerTests
{
    private const int PaceThatNeverSleeps = 0;

    private static readonly DisplayPrice WalkedBasePrice =
        Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit);

    private static readonly DisplayPrice WalkedDiscountedPrice =
        Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit);

    private static readonly int PagesPerRun = Generated.NewBatchLimitAboveAFewCandidates();

    private static readonly int ConfiguredPagesPerRun = Generated.NewBatchLimitAboveAFewCandidates();

    private static readonly int ConfiguredRewalkDays = Random.Shared.Next(2, 30);

    [Fact]
    public async Task ProcessAsync_AdmitsAFullGameAndSavesTheResumeOffset_WhenACategoryPageCarriesOne()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var product = FullGame();
        var store = new FakeStoreCatalogClient(Page(prefix, isLast: true, products: product));
        var admittedGameId = Generated.NewGameId();
        var dataSource = Database(categoryId, prefix);
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(admittedGameId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new StoreCatalogCrawlOutcome(1, 1, 1, null), outcome);
        var insert = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO games"));
        Assert.Equal(product.Name, insert.Parameters[CuratorSqlParameters.CanonicalTitle].Value);
        var enrichment = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO game_enrichment"));
        Assert.Equal(admittedGameId, enrichment.Parameters[CuratorSqlParameters.GameId].Value);
        var cache = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO psn_catalog_cache"));
        Assert.Equal(admittedGameId, cache.Parameters[CuratorSqlParameters.GameId].Value);
        Assert.Equal(product.NpTitleId, cache.Parameters[CuratorSqlParameters.TitleId].Value);
        Assert.Equal(product.Id, cache.Parameters[CuratorSqlParameters.StoreProductId].Value);
        Assert.Equal(WalkedBasePrice.Cents, cache.Parameters[CuratorSqlParameters.PriceBaseCents].Value);
        Assert.Equal(WalkedDiscountedPrice.Cents, cache.Parameters[CuratorSqlParameters.PriceDiscountedCents].Value);
        Assert.True(cache.Parameters[CuratorSqlParameters.PricePresent].Value is true, "the walk paid for this price, so it is stored");
        var progress = Assert.Single(dataSource.ExecutedCommands, Executed("UPDATE store_crawl_categories"));
        Assert.Equal(1, progress.Parameters[CuratorSqlParameters.NextOffset].Value);
        Assert.True(progress.Parameters[CuratorSqlParameters.Completed].Value is true);
    }

    [Fact]
    public async Task ProcessAsync_AdmitsNothing_WhenTheOnlyProductIsNotAFullGame()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var bundle = FullGame() with { Classification = Generated.NewGameTitle() };
        var store = new FakeStoreCatalogClient(Page(prefix, isLast: true, products: bundle));
        var dataSource = Database(categoryId, prefix);
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, outcome.GamesCreated);
        Assert.DoesNotContain(dataSource.ExecutedCommands, command => Executed("INSERT INTO games")(command));
    }

    [Fact]
    public async Task ProcessAsync_StopsAsRenamed_WhenPageOneAnswersToADifferentReportingName()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var store = new FakeStoreCatalogClient(
            Page(Generated.NewGameTitle(), isLast: true, products: FullGame()));
        var dataSource = Database(categoryId, Generated.NewGameTitle());

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StoreCatalogCrawlWorker.StoppedByRenamedCategory, outcome.StoppedReason);
        Assert.DoesNotContain(dataSource.ExecutedCommands, command => Executed("INSERT INTO games")(command));
        Assert.DoesNotContain(
            dataSource.ExecutedCommands, command => Executed("UPDATE store_crawl_categories")(command));
    }

    [Fact]
    public async Task ProcessAsync_MarksNoWalkComplete_WhenTheCategoryReportsAHugeTotalAndReturnsNoProducts()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var cappedTotal = Random.Shared.Next(1000, 10000);
        var store = new FakeStoreCatalogClient(new StoreCategoryGrid
        {
            ReportingName = prefix,
            PageInfo = new StoreCategoryPageInfo { TotalCount = cappedTotal, IsLast = false },
        });
        var dataSource = Database(categoryId, prefix);

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StoreCatalogCrawlWorker.StoppedByNoProducts, outcome.StoppedReason);
        Assert.DoesNotContain(
            dataSource.ExecutedCommands, command => Executed("UPDATE store_crawl_categories")(command));
    }

    [Fact]
    public async Task ProcessAsync_StopsAsContended_WhenAnotherCrawlHoldsTheLock()
    {
        // Arrange
        var dataSource = new FakeDbDataSource { GrantsAdvisoryLocks = false };
        var store = new FakeStoreCatalogClient();

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StoreCatalogCrawlWorker.StoppedByContention, outcome.StoppedReason);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task ProcessAsync_StopsOnTheRotatedQuery_WhenEveryPersistedHashIsRejected()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var store = new FakeStoreCatalogClient { Throws = new StoreQueryRotatedException(Generated.NewGameTitle()) };
        var dataSource = Database(categoryId, Generated.NewGameTitle());

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StoreCatalogCrawlWorker.StoppedByRotatedQuery, outcome.StoppedReason);
    }

    [Fact]
    public async Task ProcessAsync_ReadsNoSecondPage_WhenThePageBudgetIsOne()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var store = new FakeStoreCatalogClient(
            Page(prefix, isLast: false, products: FullGame()),
            Page(prefix, isLast: true, products: FullGame()));
        var existingGameId = Generated.NewGameId();
        var dataSource = Database(categoryId, prefix);
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(existingGameId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            1, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, outcome.PagesRead);
        Assert.Single(store.Requests);
    }

    [Fact]
    public void NextOffset_AdvancesByTheProductsActuallyReturned_WhenTheGatewayReportsASmallerOffset()
    {
        // Arrange
        var requestedOffset = Random.Shared.Next(100, 1000);
        var productCount = Random.Shared.Next(1, 100);
        var pageInfo = new StoreCategoryPageInfo { Offset = 0 };

        // Act
        var next = StoreCatalogCrawlWorker.NextOffset(pageInfo, requestedOffset, productCount);

        // Assert
        Assert.Equal(requestedOffset + productCount, next);
    }

    [Fact]
    public void NextOffset_StillAdvances_WhenAPageCarriesNoProducts()
    {
        // Arrange
        var requestedOffset = Random.Shared.Next(100, 1000);

        // Act
        var next = StoreCatalogCrawlWorker.NextOffset(pageInfo: null, requestedOffset, 0);

        // Assert
        Assert.Equal(requestedOffset + 1, next);
    }

    [Fact]
    public async Task ProcessAsync_ResumesAtTheSavedOffset_RatherThanWalkingTheCategoryFromTheStart()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var savedOffset = Random.Shared.Next(100, 1000);
        var store = new FakeStoreCatalogClient(Page(prefix, isLast: true));
        var dataSource = Database(new SeededCategory(categoryId, prefix, savedOffset, null));

        // Act
        await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(savedOffset, Assert.Single(store.Requests).Offset);
    }

    [Fact]
    public async Task ProcessAsync_RestartsACompletedCategoryFromTheBeginning_OnceTheRewalkIntervalHasPassed()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var completedAt = DateTimeOffset.UtcNow.AddDays(-(ConfiguredRewalkDays + 1));
        var savedOffset = Random.Shared.Next(100, 1000);
        var store = new FakeStoreCatalogClient(Page(prefix, isLast: true));
        var dataSource = Database(
            new SeededCategory(categoryId, prefix, savedOffset, completedAt));

        // Act
        await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, Assert.Single(store.Requests).Offset);
        Assert.Single(dataSource.ExecutedCommands, Executed("walk_completed_at = NULL"));
    }

    [Fact]
    public async Task ProcessAsync_LeavesACompletedCategoryAlone_WhileItIsStillInsideTheRewalkInterval()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var savedOffset = Random.Shared.Next(100, 1000);
        var store = new FakeStoreCatalogClient(Page(prefix, isLast: true));
        var dataSource = Database(
            new SeededCategory(categoryId, prefix, savedOffset, DateTimeOffset.UtcNow.AddDays(-1)));

        // Act
        await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(savedOffset, Assert.Single(store.Requests).Offset);
        Assert.DoesNotContain(
            dataSource.ExecutedCommands, command => Executed("walk_completed_at = NULL")(command));
    }

    [Fact]
    public async Task ProcessAsync_StopsOnTheTimeBudget_BeforeAskingTheStorefrontAnything()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var dataSource = Database(categoryId, prefix);
        var store = new FakeStoreCatalogClient();

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(TimeSpan.Zero), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StoreCatalogCrawlWorker.StoppedByTimeBudget, outcome.StoppedReason);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task ProcessAsync_StopsAsUnreachable_WhenTheStorefrontCannotBeReached()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var prefix = Generated.NewGameTitle();
        var dataSource = Database(categoryId, prefix);
        var store = new FakeStoreCatalogClient { Throws = new HttpRequestException(Generated.NewFailureMessage()) };

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StoreCatalogCrawlWorker.StoppedByUnreachableStore, outcome.StoppedReason);
    }

    [Fact]
    public async Task ProcessAsync_WalksTheNextCategory_WhenAnEarlierOneStopsWithNoProductsOfItsOwn()
    {
        // Arrange
        var emptyCategoryId = Guid.NewGuid();
        var walkableCategoryId = Guid.NewGuid();
        var emptyPrefix = Generated.NewGameTitle();
        var walkablePrefix = Generated.NewGameTitle();
        var bundle = FullGame() with { Classification = Generated.NewGameTitle() };
        var store = new FakeStoreCatalogClient(
            Page(emptyPrefix, isLast: false),
            Page(walkablePrefix, isLast: true, products: bundle));
        var dataSource = Database(
            new SeededCategory(emptyCategoryId, emptyPrefix, 0, null),
            new SeededCategory(walkableCategoryId, walkablePrefix, 0, null));

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(
            PagesPerRun, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StoreCatalogCrawlWorker.StoppedByNoProducts, outcome.StoppedReason);
        Assert.Equal(
            [emptyCategoryId.ToString(), walkableCategoryId.ToString()],
            store.Requests.Select(request => request.CategoryId));
    }

    private static Predicate<FakeDbCommand> Executed(string sqlFragment) =>
        command => command.ExecutedSql.Contains(sqlFragment, StringComparison.Ordinal);

    private static StoreCatalogCrawlWorker Worker(FakeDbDataSource dataSource, IStoreGatewayClient store) =>
        new(new StoreCatalogCrawlRepository(dataSource), store, Configuration());

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [CuratorConfigurationKeys.StoreCrawlPagesPerRun] = Invariant(ConfiguredPagesPerRun),
                [CuratorConfigurationKeys.StoreCrawlPaceMilliseconds] = Invariant(PaceThatNeverSleeps),
                [CuratorConfigurationKeys.StoreCrawlRewalkDays] = Invariant(ConfiguredRewalkDays),
            })
            .Build();

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static StoreCategoryProduct FullGame() => new()
    {
        Id = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix),
        Name = Generated.NewGameTitle(),
        NpTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix),
        Classification = StoreCategoryProduct.FullGameClassification,
        Price = new StoreCategoryPrice
        {
            IsFree = false,
            IsTiedToSubscription = false,
            BasePrice = WalkedBasePrice.Text,
            DiscountedPrice = WalkedDiscountedPrice.Text,
            DiscountText = Generated.NewGameTitle(),
        },
    };

    private static StoreCategoryGrid Page(string reportingName, bool isLast, params StoreCategoryProduct[] products) =>
        new()
        {
            ReportingName = reportingName,
            PageInfo = new StoreCategoryPageInfo
            {
                Offset = 0,
                TotalCount = products.Length,
                IsLast = isLast,
            },
            Products = products,
        };

    private static FakeDbDataSource Database(Guid categoryId, string reportingNamePrefix) =>
        Database(new SeededCategory(categoryId, reportingNamePrefix, 0, null));

    private static FakeDbDataSource Database(params SeededCategory[] seeded)
    {
        var categories = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(int),
            typeof(DateTimeOffset));
        foreach (var category in seeded)
        {
            categories.Rows.Add(
                category.CategoryId,
                Generated.NewGameTitle(),
                category.ReportingNamePrefix,
                category.NextOffset,
                (object?)category.WalkCompletedAt ?? DBNull.Value);
        }

        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(categories));
        return dataSource;
    }

    private sealed record SeededCategory(
        Guid CategoryId,
        string ReportingNamePrefix,
        int NextOffset,
        DateTimeOffset? WalkCompletedAt);

    private sealed class FakeStoreCatalogClient : IStoreGatewayClient
    {
        private readonly Queue<StoreCategoryGrid> _pages;

        public FakeStoreCatalogClient(params StoreCategoryGrid[] pages) =>
            _pages = new Queue<StoreCategoryGrid>(pages);

        public List<(string CategoryId, int Offset)> Requests { get; } = [];

        public Exception? Throws { get; init; }

        public Task<StoreProductNode?> ProductAsync(string productId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The crawl never asks about one product.");

        public Task<StoreStarRating?> StarRatingAsync(string productId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The crawl never asks for a rating.");

        public Task<StoreCategoryGrid> CategoryPageAsync(
            string categoryId,
            int offset,
            int size,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((categoryId, offset));
            if (Throws is not null)
            {
                throw Throws;
            }

            return Task.FromResult(_pages.Dequeue());
        }
    }
}
