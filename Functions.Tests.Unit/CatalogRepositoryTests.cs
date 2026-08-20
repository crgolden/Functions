namespace Functions.Tests.Unit;

using System.Data;
using Curator.Catalog;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class CatalogRepositoryTests
{
    [Fact]
    public async Task ReclassifyFranchiseAsync_WhenNoRuleMatchesAndTheColumnIsAlreadyNull_CountsNoUpdate()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(GamesTable(("Tetris Effect", null))));
        var repository = new CatalogRepository(dataSource);

        // Act
        var updated = await repository.ReclassifyFranchiseAsync(
            [new FranchiseRule(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), "halo", "Halo", 1)], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, updated);
        Assert.Single(dataSource.ExecutedCommands);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_WhenNoRuleMatchesAnAlreadyClassifiedGame_WritesNullNotAnEmptyString()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(GamesTable(("Tetris Effect", "Halo"))));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new CatalogRepository(dataSource);

        // Act
        var updated = await repository.ReclassifyFranchiseAsync(
            [new FranchiseRule(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), "halo", "Halo", 1)], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, updated);
        Assert.Equal(DBNull.Value, dataSource.ExecutedCommands[1].Parameters[0].Value);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_UpdatesOnlyTheGamesWhoseFranchiseActuallyChanges()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(
            GamesTable(("Halo Infinite", "Halo"), ("Halo Wars", null), ("Tetris Effect", null))));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new CatalogRepository(dataSource);

        // Act
        var updated = await repository.ReclassifyFranchiseAsync(
            [new FranchiseRule(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), "halo", "Halo", 1)], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, updated);
        Assert.Equal(2, dataSource.ExecutedCommands.Count);
    }

    [Fact]
    public async Task GetFranchiseRulesFingerprintAsync_ReadsTheFranchiseReclassificationPassRow()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult("abc123"));
        var repository = new CatalogRepository(dataSource);

        // Act
        var fingerprint = await repository.GetFranchiseRulesFingerprintAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("abc123", fingerprint);
        Assert.Contains(
            "pass_name = 'franchise_reclassification'",
            dataSource.ExecutedCommands[0].CapturedCommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFranchiseRulesFingerprintAsync_WhenThePassHasNeverRun_ReturnsNull()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        var repository = new CatalogRepository(dataSource);

        // Act
        var fingerprint = await repository.GetFranchiseRulesFingerprintAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(fingerprint);
    }

    [Fact]
    public async Task SetFranchiseRulesFingerprintAsync_UpsertsTheFranchiseReclassificationPassRow()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.SetFranchiseRulesFingerprintAsync("abc123", TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("INSERT INTO curation_rule_pass_state", sql, StringComparison.Ordinal);
        Assert.Contains("'franchise_reclassification'", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (pass_name) DO UPDATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_PrefersTheStoreCacheTitleIdOverALibraryEntrys()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.ListAllGameIdsAndTitlesAsync(TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        var storeCacheIndex = sql.IndexOf("FROM psn_catalog_cache c", StringComparison.Ordinal);
        var libraryEntryIndex = sql.IndexOf("FROM library_entries l", StringComparison.Ordinal);
        Assert.True(storeCacheIndex >= 0 && storeCacheIndex < libraryEntryIndex);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_WhenNeitherSourceKnowsATitleId_ReturnsItAsNull()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("title_id", typeof(string));
        table.Rows.Add(Guid.NewGuid(), "Tetris Effect", DBNull.Value);
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new CatalogRepository(dataSource);

        // Act
        var games = await repository.ListAllGameIdsAndTitlesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(Assert.Single(games).TitleId);
    }

    private static DataTable GamesTable(params (string CanonicalTitle, string? Franchise)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("franchise", typeof(string));
        foreach (var row in rows)
        {
            table.Rows.Add(Guid.NewGuid(), row.CanonicalTitle, (object?)row.Franchise ?? DBNull.Value);
        }

        return table;
    }
}
