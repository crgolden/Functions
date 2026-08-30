namespace Functions.Tests.Unit;

using Curator.OpenCritic;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class OpenCriticCacheRepositoryTests
{
    [Fact]
    public async Task GetCursorAsync_ReturnsZero_WhenPaginationNeverStartedForThatPlatform()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        var repository = new OpenCriticCacheRepository(dataSource);

        var cursor = await repository.GetCursorAsync("ps5", TestContext.Current.CancellationToken);

        Assert.Equal(0, cursor);
    }

    [Fact]
    public async Task GetCursorAsync_ReturnsZero_WhenTheStoredValueIsDbNull()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(DBNull.Value));
        var repository = new OpenCriticCacheRepository(dataSource);

        var cursor = await repository.GetCursorAsync("ps5", TestContext.Current.CancellationToken);

        Assert.Equal(0, cursor);
    }

    [Fact]
    public async Task GetCursorAsync_ReturnsTheStoredResumeOffset()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(140));
        var repository = new OpenCriticCacheRepository(dataSource);

        var cursor = await repository.GetCursorAsync("ps4", TestContext.Current.CancellationToken);

        Assert.Equal(140, cursor);
        Assert.Contains("platform = @platform", dataSource.ExecutedCommands[0].CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetCursorAsync_UpsertsSoConcurrentCallersShareOneCursor()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new OpenCriticCacheRepository(dataSource);

        await repository.SetCursorAsync("ps5", 220, TestContext.Current.CancellationToken);

        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("INSERT INTO opencritic_pagination_cursor", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (platform) DO UPDATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveGamesAsync_OpensNoConnection_WhenTheBatchIsEmpty()
    {
        var dataSource = new FakeDbDataSource();
        var repository = new OpenCriticCacheRepository(dataSource);

        await repository.SaveGamesAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task SaveGamesAsync_KeepsTheStoredRawPayload_WhenTheIncomingOneIsNull()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new OpenCriticCacheRepository(dataSource);

        await repository.SaveGamesAsync(
            [new OpenCriticGame(1, "Game A", 85, "Strong", 90)],
            TestContext.Current.CancellationToken);

        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("raw = COALESCE(EXCLUDED.raw, opencritic_cache.raw)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveGamesAsync_UsesOneConnectionForTheWholeBatch()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new OpenCriticCacheRepository(dataSource);

        await repository.SaveGamesAsync(
            [
                new OpenCriticGame(1, "Game A", 85, "Strong", 90),
                new OpenCriticGame(2, "Game B", null, null, null),
                new OpenCriticGame(3, "Game C", 70, "Fair", 55),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, dataSource.ConnectionsCreated);
        Assert.Equal(3, dataSource.ExecutedCommands.Count);
    }
}
