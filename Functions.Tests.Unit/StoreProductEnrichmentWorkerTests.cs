namespace Functions.Tests.Unit;

using System.Data;
using Curator.Enrichment;
using Curator.Jobs;
using Curator.Store;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class StoreProductEnrichmentWorkerTests
{
    private const int GenerousLimit = 100;

    [Fact]
    public async Task ProcessAsync_WritesTheStorefrontsAnswerIntoBothTheEnrichmentRowAndThePsnCache_WhenTheProductExists()
    {
        // Arrange
        var candidate = Candidate();
        var product = Product(candidate.StoreProductId);
        var rating = new StoreStarRating { AverageRating = TestValues.NewStarRating(), TotalRatingsCount = TestValues.NewPsnRatingCount() };
        var store = new FakeStoreGatewayClient { Products = { [candidate.StoreProductId] = product }, Ratings = { [candidate.StoreProductId] = rating } };
        var dataSource = Database(candidate);
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        var outcome = await Worker(dataSource, store).ProcessAsync(GenerousLimit, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new StoreProductPassOutcome(1, 0, 0, null), outcome);
        var enrichment = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO game_enrichment"));
        Assert.Equal(Guid.Parse(candidate.GameId), enrichment.Parameters["@game_id"].Value);
        Assert.Equal(product.PublisherName, enrichment.Parameters["@publisher"].Value);
        Assert.Equal(rating.AverageRating, enrichment.Parameters["@psn_rating"].Value);
        Assert.Equal(rating.TotalRatingsCount, enrichment.Parameters["@psn_rating_count"].Value);
        Assert.True(enrichment.Parameters["@psn_enriched"].Value is true);
        Assert.True(enrichment.Parameters["@psn_attempted"].Value is true);
        var cache = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO psn_catalog_cache"));
        Assert.Equal(candidate.TitleId, cache.Parameters["@title_id"].Value);
        Assert.Equal(product.Concept?.Id, cache.Parameters["@concept_id"].Value);
        Assert.Equal(product.Type, cache.Parameters["@concept_type"].Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task ProcessAsync_WritesNoRating_WhenNobodyHasRatedTheProduct(int? ratingsCount)
    {
        // Arrange
        var candidate = Candidate();
        var product = Product(candidate.StoreProductId);
        var rating = new StoreStarRating { AverageRating = TestValues.NewStarRating(), TotalRatingsCount = ratingsCount };
        var store = new FakeStoreGatewayClient { Products = { [candidate.StoreProductId] = product }, Ratings = { [candidate.StoreProductId] = rating } };
        var dataSource = Database(candidate);
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));

        // Act
        await Worker(dataSource, store).ProcessAsync(GenerousLimit, TimeSpan.Zero, new JobTimeBudget(), TestContext.Current.CancellationToken);

        // Assert
        var enrichment = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO game_enrichment"));
        Assert.Same(DBNull.Value, enrichment.Parameters["@psn_rating"].Value);
        var cache = Assert.Single(dataSource.ExecutedCommands, Executed("INSERT INTO psn_catalog_cache"));
        Assert.Same(DBNull.Value, cache.Parameters["@star_rating"].Value);
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
        Assert.True(enrichment.Parameters["@psn_enriched"].Value is false);
        Assert.True(enrichment.Parameters["@psn_attempted"].Value is true);
        Assert.DoesNotContain(dataSource.ExecutedCommands, Executed("INSERT INTO psn_catalog_cache"));
        Assert.Empty(store.RatingRequests);
    }

    [Fact]
    public async Task ProcessAsync_StopsAndReportsTheRemainder_WhenTheStorefrontRotatedThePersistedQuery()
    {
        // Arrange
        var candidates = new[] { Candidate(), Candidate() };
        var store = new FakeStoreGatewayClient { Throws = new StoreQueryRotatedException(TestValues.NewErrorMessage()) };
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
        new(new EnrichmentRepository(dataSource), store);

    private static StoreProductCandidate Candidate() =>
        new(Guid.NewGuid().ToString(), TestValues.NewGameTitle(), TestValues.NewTitleId(), TestValues.NewStoreProductId());

    private static StoreProductNode Product(string productId) => new()
    {
        Id = productId,
        Name = TestValues.NewGameTitle(),
        PublisherName = TestValues.NewPublisher(),
        ReleaseDate = TestValues.NewReleaseTimestamp().ToString("O"),
        Type = TestValues.NewConceptType(),
        Concept = new StoreConcept { Id = TestValues.NewConceptId() },
    };

    private static FakeDbDataSource Database(params StoreProductCandidate[] candidates)
    {
        var worklist = new DataTable();
        worklist.Columns.Add("game_id", typeof(Guid));
        worklist.Columns.Add("canonical_title", typeof(string));
        worklist.Columns.Add("title_id", typeof(string));
        worklist.Columns.Add("store_product_id", typeof(string));
        foreach (var candidate in candidates)
        {
            worklist.Rows.Add(Guid.Parse(candidate.GameId), candidate.Title, candidate.TitleId, candidate.StoreProductId);
        }

        var genres = new DataTable();
        genres.Columns.Add("genre_id", typeof(Guid));
        genres.Columns.Add("name", typeof(string));
        genres.Columns.Add("display_name", typeof(string));
        genres.Columns.Add("priority", typeof(int));

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
    }
}
