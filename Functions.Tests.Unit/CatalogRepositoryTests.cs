namespace Functions.Tests.Unit;

using System.Data;
using Curator.Catalog;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class CatalogRepositoryTests
{
    [Fact]
    public async Task ReclassifyFranchiseAsync_WhenNoRuleMatchesAndTheColumnIsAlreadyNull_SendsNoUpdate()
    {
        // Arrange
        var unclassifiedGameId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var rule = new FranchiseRule(ruleId, TestValues.NewDigitsOnlyToken(), TestValues.NewFranchiseName(), TestValues.NewRulePriority());
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(GamesTable((unclassifiedGameId, TestValues.NewGameTitle(), null))));
        var repository = new CatalogRepository(dataSource);

        // Act
        var updated = await repository.ReclassifyFranchiseAsync([rule], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, updated);
        Assert.Single(dataSource.ExecutedCommands);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_WhenNoRuleMatchesAnAlreadyClassifiedGame_WritesNullNotAnEmptyString()
    {
        // Arrange
        var declassifiedGameId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var rule = new FranchiseRule(ruleId, TestValues.NewDigitsOnlyToken(), TestValues.NewFranchiseName(), TestValues.NewRulePriority());
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(
            GamesTable((declassifiedGameId, TestValues.NewGameTitle(), TestValues.NewFranchiseName()))));
        var repository = new CatalogRepository(dataSource);

        // Act
        var updated = await repository.ReclassifyFranchiseAsync([rule], TestContext.Current.CancellationToken);

        // Assert
        var update = FranchiseUpdate(dataSource);
        var gameIds = Assert.IsType<Guid[]>(update.Parameters["@game_ids"].Value);
        Assert.Equal(declassifiedGameId, Assert.Single(gameIds));
        Assert.Null(Assert.Single(Assert.IsType<string[]>(update.Parameters["@franchises"].Value)));
        Assert.Equal(gameIds.Length, updated);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_UpdatesOnlyTheGamesWhoseFranchiseActuallyChanges_InOneStatement()
    {
        // Arrange
        var pattern = TestValues.NewDigitsOnlyToken();
        var franchise = TestValues.NewFranchiseName();
        var ruleId = Guid.NewGuid();
        var rule = new FranchiseRule(ruleId, pattern, franchise, TestValues.NewRulePriority());
        var alreadyClassifiedGameId = Guid.NewGuid();
        var newlyMatchedGameId = Guid.NewGuid();
        var unmatchedGameId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(GamesTable(
            (alreadyClassifiedGameId, $"{pattern} {TestValues.NewGameTitle()}", franchise),
            (newlyMatchedGameId, $"{TestValues.NewGameTitle()} {pattern}", null),
            (unmatchedGameId, TestValues.NewGameTitle(), null))));
        var repository = new CatalogRepository(dataSource);

        // Act
        var updated = await repository.ReclassifyFranchiseAsync([rule], TestContext.Current.CancellationToken);

        // Assert
        var update = FranchiseUpdate(dataSource);
        var gameIds = Assert.IsType<Guid[]>(update.Parameters["@game_ids"].Value);
        Assert.Equal([newlyMatchedGameId], gameIds);
        Assert.Equal([franchise], Assert.IsType<string[]>(update.Parameters["@franchises"].Value));
        Assert.Equal(gameIds.Length, updated);
        Assert.Contains("unnest(@game_ids, @franchises)", update.ExecutedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFranchiseRulesFingerprintAsync_ReadsTheFranchiseReclassificationPassRow()
    {
        // Arrange
        var storedFingerprint = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(storedFingerprint));
        var repository = new CatalogRepository(dataSource);

        // Act
        var fingerprint = await repository.GetFranchiseRulesFingerprintAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(storedFingerprint, fingerprint);
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
        var fingerprint = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        var repository = new CatalogRepository(dataSource);

        // Act
        await repository.SetFranchiseRulesFingerprintAsync(fingerprint, TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Contains("INSERT INTO curation_rule_pass_state", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Contains("'franchise_reclassification'", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (pass_name) DO UPDATE", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Equal(fingerprint, command.Parameters["@fingerprint"].Value);
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
        var sql = Assert.Single(dataSource.ExecutedCommands).ExecutedSql;
        var storeCacheIndex = sql.IndexOf("FROM psn_catalog_cache c", StringComparison.Ordinal);
        var libraryEntryIndex = sql.IndexOf("FROM library_entries l", StringComparison.Ordinal);
        Assert.True(storeCacheIndex >= 0 && storeCacheIndex < libraryEntryIndex);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_WhenNeitherSourceKnowsATitleId_ReturnsItAsNull()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("title_id", typeof(string));
        table.Rows.Add(gameId, TestValues.NewGameTitle(), DBNull.Value);
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new CatalogRepository(dataSource);

        // Act
        var games = await repository.ListAllGameIdsAndTitlesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(Assert.Single(games).TitleId);
    }

    private static FakeDbCommand FranchiseUpdate(FakeDbDataSource dataSource) =>
        Assert.Single(dataSource.ExecutedCommands, command => command.ExecutedSql.Contains("UPDATE games", StringComparison.Ordinal));

    private static DataTable GamesTable(params (Guid GameId, string CanonicalTitle, string? Franchise)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("franchise", typeof(string));
        foreach (var row in rows)
        {
            table.Rows.Add(row.GameId, row.CanonicalTitle, (object?)row.Franchise ?? DBNull.Value);
        }

        return table;
    }
}
