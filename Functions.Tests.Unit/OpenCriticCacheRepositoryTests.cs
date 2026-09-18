namespace Functions.Tests.Unit;

using Curator.OpenCritic;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class OpenCriticCacheRepositoryTests
{
    [Fact]
    public async Task GetCursorAsync_ReturnsZero_WhenPaginationNeverStartedForThatPlatform()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        var repository = new OpenCriticCacheRepository(dataSource);

        // Act
        var cursor = await repository.GetCursorAsync(OpenCriticPlatforms.Ps5, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, cursor);
    }

    [Fact]
    public async Task GetCursorAsync_ReturnsZero_WhenTheStoredValueIsDbNull()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(DBNull.Value));
        var repository = new OpenCriticCacheRepository(dataSource);

        // Act
        var cursor = await repository.GetCursorAsync(OpenCriticPlatforms.Ps5, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, cursor);
    }

    [Fact]
    public async Task GetCursorAsync_ReturnsTheStoredResumeOffset()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var storedCursor = TestValues.NewPaginationCursor();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(storedCursor));
        var repository = new OpenCriticCacheRepository(dataSource);

        // Act
        var cursor = await repository.GetCursorAsync(OpenCriticPlatforms.Ps4, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(storedCursor, cursor);
        Assert.Contains("platform = @platform", dataSource.ExecutedCommands[0].CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetCursorAsync_UpsertsSoConcurrentCallersShareOneCursor()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new OpenCriticCacheRepository(dataSource);

        // Act
        await repository.SetCursorAsync(
            OpenCriticPlatforms.Ps5, TestValues.NewPaginationCursor(), TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("INSERT INTO opencritic_pagination_cursor", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (platform) DO UPDATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveGamesAsync_OpensNoConnection_WhenTheBatchIsEmpty()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new OpenCriticCacheRepository(dataSource);

        // Act
        await repository.SaveGamesAsync([], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task SaveGamesAsync_KeepsTheStoredRawPayload_WhenTheIncomingOneIsNull()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new OpenCriticCacheRepository(dataSource);

        // Act
        await repository.SaveGamesAsync([ScoredGame()], TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("raw = COALESCE(EXCLUDED.raw, opencritic_cache.raw)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveGamesAsync_UsesOneConnectionForTheWholeBatch()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new OpenCriticCacheRepository(dataSource);
        OpenCriticGame[] games = [ScoredGame(), UnscoredGame(), ScoredGame()];

        // Act
        await repository.SaveGamesAsync(games, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, dataSource.ConnectionsCreated);
        Assert.Equal(games.Length, dataSource.ExecutedCommands.Count);
    }

    private static OpenCriticGame ScoredGame() =>
        new(
            TestValues.NewOpenCriticGameId(),
            TestValues.NewGameTitle(),
            TestValues.NewOpenCriticScore(),
            TestValues.NewOpenCriticTier(),
            TestValues.NewPercentRecommended());

    private static OpenCriticGame UnscoredGame() =>
        new(TestValues.NewOpenCriticGameId(), TestValues.NewGameTitle(), null, null, null);
}
