namespace Functions.Tests.Unit;

using Functions.Curator;
using Functions.Curator.Catalog;
using Functions.Tests.Unit.TestSupport;

[Trait("Category", "Unit")]
public sealed class CatalogRepositoryCanonicalizationTests
{
    [Fact]
    public async Task GetEditionRanksAsync_ReadsTheKeywordToRankMapping()
    {
        // Arrange
        var table = FakeResultSet.WithColumns(
            typeof(string),
            typeof(int));
        var keyword = Generated.NewEditionKeyword();
        var rank = Generated.NewEditionRank();
        table.Rows.Add(keyword, rank);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));

        // Act
        var ranks = await new CatalogRepository(dataSource)
            .GetEditionRanksAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rank, ranks[keyword]);
    }

    [Fact]
    public async Task GetNameOverridesAsync_ReadsTheProductToCorrectedNameMapping()
    {
        // Arrange
        var table = FakeResultSet.WithColumns(
            typeof(string),
            typeof(string),
            typeof(string));
        var conceptId = Generated.NewConceptId();
        var productId = Generated.NewProductId();
        var overrideName = Generated.NewOverrideName();
        table.Rows.Add(conceptId, productId, overrideName);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));

        // Act
        var overrides = await new CatalogRepository(dataSource)
            .GetNameOverridesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(overrideName, overrides[new NameOverrideKey(conceptId, productId)]);
    }

    [Fact]
    public async Task GetGloballyExcludedConceptIdsAsync_ReadsEveryPermanentlyExcludedConcept()
    {
        // Arrange
        var table = FakeResultSet.WithColumns(
            typeof(string));
        var firstConceptIdInOrder = Generated.NewConceptIdSortingFirst();
        var lastConceptIdInOrder = Generated.NewConceptIdSortingLast();
        table.Rows.Add(lastConceptIdInOrder);
        table.Rows.Add(firstConceptIdInOrder);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));

        // Act
        var excluded = await new CatalogRepository(dataSource)
            .GetGloballyExcludedConceptIdsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([firstConceptIdInOrder, lastConceptIdInOrder], excluded.Order());
    }

    [Fact]
    public async Task UpsertGameAsync_ResolvesAnExistingGameByItsConceptIdAndTitleTogetherBeforeTryingTheTitleAlone()
    {
        // Arrange
        var existing = Guid.NewGuid();
        var lowercasedTitle = Generated.NewLongTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(existing));
        var repository = new CatalogRepository(dataSource);

        // Act
        var gameId = await repository.UpsertGameAsync(
            Game(lowercasedTitle, [Generated.NewConceptId()]),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(existing, gameId);
        var byConcept = Only(dataSource, "FROM game_concepts");
        Assert.Contains("g.normalized_title = @normalized_title", byConcept.ExecutedSql, StringComparison.Ordinal);
        Assert.Equal(lowercasedTitle, byConcept.Parameters[CuratorSqlParameters.NormalizedTitle].Value);
        Assert.DoesNotContain(dataSource.ExecutedCommands, Executed("FROM games WHERE normalized_title"));
    }

    [Fact]
    public async Task UpsertGameAsync_FallsBackToTheNormalisedTitle_WhenNoConceptResolves()
    {
        // Arrange
        var existing = Guid.NewGuid();
        var lowercasedTitle = Generated.NewLongTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(existing));
        var repository = new CatalogRepository(dataSource);

        // Act
        var gameId = await repository.UpsertGameAsync(
            Game(lowercasedTitle.ToUpperInvariant(), [Generated.NewConceptId()]),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(existing, gameId);
        Assert.Equal(
            lowercasedTitle,
            Only(dataSource, "FROM games WHERE normalized_title").Parameters[CuratorSqlParameters.NormalizedTitle].Value);
    }

    [Fact]
    public async Task UpsertGameAsync_NormalisesTheTitleByTrimmingAndLowercasing()
    {
        // Arrange
        var lowercasedTitle = Generated.NewLongTitle();
        var sameTitleUppercasedAndPadded = $"  {lowercasedTitle.ToUpperInvariant()}  ";
        var resolvedGameId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(resolvedGameId));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game(sameTitleUppercasedAndPadded, []), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            lowercasedTitle,
            Only(dataSource, "FROM games WHERE normalized_title").Parameters[CuratorSqlParameters.NormalizedTitle].Value);
    }

    [Fact]
    public async Task UpsertGameAsync_InsertsANewGame_WhenNeitherConceptNorTitleResolves()
    {
        // Arrange
        var inserted = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(inserted));
        var repository = new CatalogRepository(dataSource);

        // Act
        var gameId = await repository.UpsertGameAsync(
            Game(Generated.NewLongTitle(), []), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(inserted, gameId);
        Assert.Contains(dataSource.ExecutedCommands, Executed("INSERT INTO games"));
    }

    [Fact]
    public async Task UpsertGameAsync_WritesTheContentKindOnInsert_AndOnlyOverwritesAKnownKindOnUpdate()
    {
        // Arrange
        var insertedGameId = Guid.NewGuid();
        var existingGameId = Guid.NewGuid();
        var inserting = new FakeDbDataSource();
        inserting.Enqueue(FakeDbCommand.WithScalarResult(null));
        inserting.Enqueue(FakeDbCommand.WithScalarResult(insertedGameId));
        var updating = new FakeDbDataSource();
        updating.Enqueue(FakeDbCommand.WithScalarResult(existingGameId));
        var mediaApp = Game(Generated.NewLongTitle(), []) with { ContentKind = ContentKind.MediaApp };

        // Act
        await new CatalogRepository(inserting).UpsertGameAsync(mediaApp, TestContext.Current.CancellationToken);
        await new CatalogRepository(updating).UpsertGameAsync(mediaApp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ContentKinds.MediaApp, Only(inserting, "INSERT INTO games").Parameters[CuratorSqlParameters.ContentKind].Value);
        var update = Only(updating, "UPDATE games SET");
        Assert.Equal(ContentKinds.MediaApp, update.Parameters[CuratorSqlParameters.ContentKind].Value);
        Assert.Contains("content_kind = COALESCE(@content_kind, games.content_kind)", update.ExecutedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertGameAsync_StoresAnAbsentFranchiseAsNullRatherThanAnEmptyString()
    {
        // Arrange
        var insertedGameId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(insertedGameId));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game(Generated.NewLongTitle(), [], franchise: string.Empty),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DBNull.Value, Only(dataSource, "INSERT INTO games").Parameters[CuratorSqlParameters.Franchise].Value);
    }

    [Fact]
    public async Task UpsertGameAsync_TakesATitleScopedAdvisoryLockInTheSameTransaction_BeforeReadingOrWriting()
    {
        // Arrange
        var lowercasedTitle = Generated.NewLongTitle();
        var sameTitleUppercasedAndPadded = $"  {lowercasedTitle.ToUpperInvariant()}  ";
        var resolvedGameId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(resolvedGameId));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game(sameTitleUppercasedAndPadded, []), TestContext.Current.CancellationToken);

        // Assert
        var lockCommand = dataSource.ExecutedCommands[0];
        Assert.Contains(
            AdvisoryLockHandle.TransactionScopedFunctionName,
            lockCommand.CapturedCommandText,
            StringComparison.Ordinal);
        Assert.Equal(CuratorAdvisoryLocks.GameUpsert, lockCommand.Parameters[CuratorSqlParameters.LockClass].Value);
        Assert.Equal(lowercasedTitle, lockCommand.Parameters[CuratorSqlParameters.LockKey].Value);
        Assert.NotNull(lockCommand.Transaction);
        var transaction = Assert.IsType<FakeDbTransaction>(lockCommand.Transaction);
        Assert.Equal(1, transaction.CommitCount);
    }

    [Fact]
    public async Task UpsertGameAsync_LinksEveryConceptToTheResolvedGameWithoutAConflictTarget()
    {
        // Arrange
        var existing = Guid.NewGuid();
        var conceptIds = new[] { Generated.NewConceptId(), Generated.NewConceptId() };
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(existing));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game(Generated.NewLongTitle(), conceptIds), TestContext.Current.CancellationToken);

        // Assert
        var links = dataSource.ExecutedCommands
            .Where(command => command.ExecutedSql
                .Contains("INSERT INTO game_concepts", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(conceptIds.Length, links.Count);
        Assert.All(links, link => Assert.Contains("ON CONFLICT DO NOTHING", link.ExecutedSql, StringComparison.Ordinal));
        Assert.All(links, link => Assert.DoesNotContain("DO UPDATE", link.ExecutedSql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertGameAsync_RunsEveryStatementOnOneConnection()
    {
        // Arrange
        var resolvedGameId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(resolvedGameId));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game(Generated.NewLongTitle(), [Generated.NewConceptId(), Generated.NewConceptId()]),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, dataSource.ConnectionsCreated);
    }

    private static Predicate<FakeDbCommand> Executed(string sqlFragment) =>
        command => command.ExecutedSql.Contains(sqlFragment, StringComparison.Ordinal);

    private static FakeDbCommand Only(FakeDbDataSource dataSource, string sqlFragment) =>
        Assert.Single(dataSource.ExecutedCommands, Executed(sqlFragment));

    private static CanonicalGame Game(
        string title,
        IReadOnlyList<string> conceptIds,
        string? franchise = null) =>
        new(
            title,
            NativePs5: true,
            Ps4Eligible: false,
            franchise ?? Generated.NewFranchiseName(),
            ProductId: Generated.NewProductId(),
            conceptIds,
            WinningEntitlementId: Generated.NewEntitlementId());
}
