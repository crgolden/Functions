namespace Functions.Tests.Unit;

using System.Data;
using Curator;
using Curator.Catalog;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class CatalogRepositoryCanonicalizationTests
{
    private static readonly string MediaAppPattern = $"App{Guid.NewGuid():N}";

    [Fact]
    public async Task ListExclusionRulesAsync_ReadsEveryRuleTypeAndPattern()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("rule_id", typeof(Guid));
        table.Columns.Add("rule_type", typeof(string));
        table.Columns.Add("pattern", typeof(string));
        table.Rows.Add(Guid.NewGuid(), ExclusionRules.MediaApp, MediaAppPattern);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));

        // Act
        var rules = await new CatalogRepository(dataSource)
            .ListExclusionRulesAsync(TestContext.Current.CancellationToken);

        // Assert
        var rule = Assert.Single(rules);
        Assert.Equal(ExclusionRules.MediaApp, rule.RuleType);
        Assert.Equal(MediaAppPattern, rule.Pattern);
    }

    [Fact]
    public async Task GetEditionRanksAsync_ReadsTheKeywordToRankMapping()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("keyword", typeof(string));
        table.Columns.Add("rank", typeof(int));
        table.Rows.Add("game of the year", 1);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));

        // Act
        var ranks = await new CatalogRepository(dataSource)
            .GetEditionRanksAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, ranks["game of the year"]);
    }

    [Fact]
    public async Task GetNameOverridesAsync_ReadsTheConceptToCorrectedNameMapping()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("concept_id", typeof(string));
        table.Columns.Add("override_name", typeof(string));
        table.Rows.Add("c1", "Corrected Name");
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));

        // Act
        var overrides = await new CatalogRepository(dataSource)
            .GetNameOverridesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Corrected Name", overrides["c1"]);
    }

    [Fact]
    public async Task GetGloballyExcludedConceptIdsAsync_ReadsEveryPermanentlyExcludedConcept()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("concept_id", typeof(string));
        table.Rows.Add("c1");
        table.Rows.Add("c2");
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));

        // Act
        var excluded = await new CatalogRepository(dataSource)
            .GetGloballyExcludedConceptIdsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["c1", "c2"], excluded.Order());
    }

    [Fact]
    public async Task UpsertGameAsync_ResolvesAnExistingGameByItsConceptIdBeforeTryingTheTitle()
    {
        // Arrange
        var existing = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(existing));
        var repository = new CatalogRepository(dataSource);

        // Act
        var gameId = await repository.UpsertGameAsync(
            Game("Bloodborne", ["c1"]), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(existing.ToString(), gameId);
        Assert.Contains(dataSource.ExecutedCommands, Executed("FROM game_concepts"));
        Assert.DoesNotContain(dataSource.ExecutedCommands, Executed("FROM games WHERE normalized_title"));
    }

    [Fact]
    public async Task UpsertGameAsync_FallsBackToTheNormalisedTitle_WhenNoConceptResolves()
    {
        // Arrange
        var existing = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(existing));
        var repository = new CatalogRepository(dataSource);

        // Act
        var gameId = await repository.UpsertGameAsync(
            Game("Bloodborne", ["c1"]), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(existing.ToString(), gameId);
        Assert.Equal(
            "bloodborne",
            Only(dataSource, "FROM games WHERE normalized_title").Parameters["@normalized_title"].Value);
    }

    [Fact]
    public async Task UpsertGameAsync_NormalisesTheTitleByTrimmingAndLowercasing()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(Guid.NewGuid()));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game("  Bloodborne  ", []), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            "bloodborne",
            Only(dataSource, "FROM games WHERE normalized_title").Parameters["@normalized_title"].Value);
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
            Game("Brand New Game", []), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(inserted.ToString(), gameId);
        Assert.Contains(dataSource.ExecutedCommands, Executed("INSERT INTO games"));
    }

    [Fact]
    public async Task UpsertGameAsync_StoresAnAbsentFranchiseAsNullRatherThanAnEmptyString()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(Guid.NewGuid()));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game("Brand New Game", [], franchise: string.Empty), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(DBNull.Value, Only(dataSource, "INSERT INTO games").Parameters["@franchise"].Value);
    }

    [Fact]
    public async Task UpsertGameAsync_TakesATitleScopedAdvisoryLockInTheSameTransaction_BeforeReadingOrWriting()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(Guid.NewGuid()));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(Game("  Bloodborne  ", []), TestContext.Current.CancellationToken);

        // Assert
        var lockCommand = dataSource.ExecutedCommands[0];
        Assert.Contains(
            AdvisoryLockHandle.TransactionScopedFunctionName,
            lockCommand.CapturedCommandText,
            StringComparison.Ordinal);
        Assert.Equal(CuratorAdvisoryLocks.GameUpsert, lockCommand.Parameters["@lock_class"].Value);
        Assert.Equal("bloodborne", lockCommand.Parameters["@lock_key"].Value);
        Assert.NotNull(lockCommand.Transaction);
        var transaction = Assert.IsType<FakeDbTransaction>(lockCommand.Transaction);
        Assert.Equal(1, transaction.CommitCount);
    }

    [Fact]
    public async Task UpsertGameAsync_RepointsEveryConceptAtTheResolvedGame()
    {
        // Arrange
        var existing = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(existing));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game("Bloodborne", ["c1", "c2"]), TestContext.Current.CancellationToken);

        // Assert
        var links = dataSource.ExecutedCommands
            .Where(command => command.ExecutedSql
                .Contains("INSERT INTO game_concepts", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public async Task UpsertGameAsync_RunsEveryStatementOnOneConnection()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(Guid.NewGuid()));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.UpsertGameAsync(
            Game("Bloodborne", ["c1", "c2"]), TestContext.Current.CancellationToken);

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
        string franchise = "None") =>
        new(
            title,
            NativePs5: true,
            Ps4Eligible: false,
            franchise,
            ProductId: "prod-1",
            conceptIds,
            WinningEntitlementId: "e1");
}
