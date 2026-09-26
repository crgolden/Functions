namespace Functions.Tests.Unit;

using Functions.Curator;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.Psn;
using Functions.Curator.Store;
using Functions.Tests.Unit.TestSupport;

[Trait("Category", "Unit")]
public sealed class StoreProductEnrichmentWorkerTests
{
    private static readonly int GenerousLimit = Generated.NewBatchLimitAboveAFewCandidates();

    [Fact]
    public async Task ProcessAsync_WritesTheStorefrontsAnswerIntoBothTheEnrichmentRowAndThePsnCache_WhenTheProductExists()
    {
        // Arrange
        var candidate = Candidate();
        var product = Product(candidate.StoreProductId);
        var rating = new StoreStarRating { AverageRating = Generated.NewStarRating(), TotalRatingsCount = Generated.NewPsnRatingCount() };
        var store = new FakeStoreGatewayClient { Products = { [candidate.StoreProductId] = product }, Ratings = { [candidate.StoreProductId] = rating } };
        var dataSource = Database(candidate);
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(GenerousLimit, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new StoreProductPassOutcome(1, 0, 0, null), outcome);
        var enrichment = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO game_enrichment"));
        Assert.Equal(candidate.GameId, enrichment.Parameters[CuratorSqlParameters.GameId].Value);
        Assert.Equal(product.PublisherName, enrichment.Parameters[CuratorSqlParameters.Publisher].Value);
        Assert.Equal(rating.AverageRating, enrichment.Parameters[CuratorSqlParameters.PsnRating].Value);
        Assert.Equal(rating.TotalRatingsCount, enrichment.Parameters[CuratorSqlParameters.PsnRatingCount].Value);
        Assert.True(enrichment.Parameters[CuratorSqlParameters.PsnEnriched].Value is true);
        Assert.True(enrichment.Parameters[CuratorSqlParameters.PsnAttempted].Value is true);
        var cache = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO psn_catalog_cache"));
        Assert.Equal(candidate.TitleId, cache.Parameters[CuratorSqlParameters.TitleId].Value);
        Assert.Equal(product.Concept?.Id, cache.Parameters[CuratorSqlParameters.ConceptId].Value);
        Assert.Equal(product.Type, cache.Parameters[CuratorSqlParameters.ConceptType].Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task ProcessAsync_WritesNoRating_WhenNobodyHasRatedTheProduct(int? ratingsCount)
    {
        // Arrange
        var candidate = Candidate();
        var product = Product(candidate.StoreProductId);
        var rating = new StoreStarRating { AverageRating = Generated.NewStarRating(), TotalRatingsCount = ratingsCount };
        var store = new FakeStoreGatewayClient { Products = { [candidate.StoreProductId] = product }, Ratings = { [candidate.StoreProductId] = rating } };
        var dataSource = Database(candidate);
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        await Worker(dataSource, store).ProcessAsync(GenerousLimit, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        var enrichment = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO game_enrichment"));
        Assert.Same(DBNull.Value, enrichment.Parameters[CuratorSqlParameters.PsnRating].Value);
        var cache = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO psn_catalog_cache"));
        Assert.Same(DBNull.Value, cache.Parameters[CuratorSqlParameters.StarRating].Value);
    }

    [Fact]
    public async Task ProcessAsync_RecordsTheAttemptWithoutEnriching_WhenTheStorefrontHasNoProductNode()
    {
        // Arrange
        var candidate = Candidate();
        var store = new FakeStoreGatewayClient();
        var dataSource = Database(candidate);
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(GenerousLimit, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new StoreProductPassOutcome(0, 1, 0, null), outcome);
        var enrichment = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO game_enrichment"));
        Assert.True(enrichment.Parameters[CuratorSqlParameters.PsnEnriched].Value is false);
        Assert.True(enrichment.Parameters[CuratorSqlParameters.PsnAttempted].Value is true);
        Assert.DoesNotContain(dataSource.ExecutedCommands, Executed("INSERT INTO psn_catalog_cache"));
        Assert.Empty(store.RatingRequests);
    }

    [Fact]
    public async Task ProcessAsync_StopsAndReportsTheRemainder_WhenTheStorefrontRotatedThePersistedQuery()
    {
        // Arrange
        var candidates = new[] { Candidate(), Candidate() };
        var store = new FakeStoreGatewayClient { Throws = new StoreQueryRotatedException(Generated.NewErrorMessage()) };
        var dataSource = Database(candidates);

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(GenerousLimit, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            new StoreProductPassOutcome(0, 0, candidates.Length, StoreProductEnrichmentWorker.StoppedByRotatedQuery),
            outcome);
        Assert.DoesNotContain(dataSource.ExecutedCommands, Executed("INSERT INTO game_enrichment"));
    }

    [Fact]
    public async Task ProcessAsync_AsksNothing_WhenAnotherPassHoldsTheLock()
    {
        // Arrange
        var store = new FakeStoreGatewayClient();
        var dataSource = new FakeDbDataSource { GrantsAdvisoryLocks = false };

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(GenerousLimit, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StoreProductEnrichmentWorker.StoppedByContention, outcome.StoppedReason);
        Assert.Empty(store.ProductRequests);
    }

    [Fact]
    public async Task ProcessAsync_StopsBeforeAskingTheStore_WhenTheTimeBudgetIsAlreadySpent()
    {
        // Arrange
        var candidate = Candidate();
        var store = new FakeStoreGatewayClient { Products = { [candidate.StoreProductId] = Product(candidate.StoreProductId) } };
        var dataSource = Database(candidate);

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(GenerousLimit, TimeSpan.Zero, new JobTimeBudget(TimeSpan.Zero), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new StoreProductPassOutcome(0, 0, 1, StoreProductEnrichmentWorker.StoppedByTimeBudget), outcome);
        Assert.Empty(store.ProductRequests);
    }

    private static Predicate<FakeDbCommand> Executed(string sqlFragment) =>
        command => command.ExecutedSql.Contains(sqlFragment, StringComparison.Ordinal);

    private static StoreProductEnrichmentWorker Worker(FakeDbDataSource dataSource, IStoreGatewayClient store) =>
        new(new EnrichmentRepository(dataSource), store, TelemetryHarness.Shared.Telemetry);

    private static StoreProductCandidate Candidate() =>
        new(Generated.NewGameId(), Generated.NewGameTitle(), Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix));

    private static StoreProductNode Product(string productId) => new()
    {
        Id = productId,
        Name = Generated.NewGameTitle(),
        PublisherName = Generated.NewPublisher(),
        ReleaseDate = Generated.NewReleaseTimestamp(),
        Type = Generated.NewConceptType(),
        Concept = new StoreConcept { Id = Generated.NewConceptId() },
    };

    private static FakeDbDataSource Database(params StoreProductCandidate[] candidates)
    {
        var worklist = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(string));
        foreach (var candidate in candidates)
        {
            worklist.Rows.Add(candidate.GameId, candidate.Title, candidate.TitleId, candidate.StoreProductId);
        }

        var genres = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(int));

        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(worklist));
        dataSource.Enqueue(FakeDbCommand.WithReader(genres));
        return dataSource;
    }

    private sealed class FakeStoreGatewayClient : IStoreGatewayClient
    {
        public Dictionary<string, StoreProductNode> Products { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, StoreStarRating> Ratings { get; } = new(StringComparer.Ordinal);

        public List<string> ProductRequests { get; } = [];

        public List<string> RatingRequests { get; } = [];

        public Exception? Throws { get; init; }

        public Task<StoreProductNode?> ProductAsync(string productId, CancellationToken cancellationToken = default)
        {
            ProductRequests.Add(productId);
            if (Throws is not null)
            {
                throw Throws;
            }

            return Task.FromResult(Products.GetValueOrDefault(productId));
        }

        public Task<StoreStarRating?> StarRatingAsync(string productId, CancellationToken cancellationToken = default)
        {
            RatingRequests.Add(productId);
            return Task.FromResult(Ratings.GetValueOrDefault(productId));
        }

        public Task<StoreCategoryGrid> CategoryPageAsync(
            string categoryId,
            int offset,
            int size,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This pass never walks a category.");
    }
}
