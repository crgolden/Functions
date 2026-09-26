namespace Functions.Tests.Unit;

using System.Data;
using Functions.Curator;
using Functions.Curator.Psn;
using Functions.Tests.Unit.TestSupport;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class PsnLinkRepositoryTests
{
    [Fact]
    public async Task GetLinkAsync_ReturnsNull_WhenNoRowExists()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var repository = new PsnLinkRepository(dataSource);

        // Act
        var link = await repository.GetLinkAsync(
            NewIdentitySub(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(link);
    }

    [Fact]
    public async Task GetLinkAsync_ReturnsTheTokenAndHarvestFlag_WhenARowExists()
    {
        // Arrange
        var table = FakeResultSet.WithColumns(
            typeof(byte[]),
            typeof(bool));
        var tokenResponseEnc = NewCiphertext();
        table.Rows.Add(tokenResponseEnc, true);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new PsnLinkRepository(dataSource);

        // Act
        var link = await repository.GetLinkAsync(
            NewIdentitySub(), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(link);
        Assert.Equal(tokenResponseEnc, link.TokenResponseEnc);
        Assert.True(link.HarvestTrophies);
    }

    [Fact]
    public async Task UpdateTokenAsync_ReturnsTrue_WhenARowWasUpdated()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new PsnLinkRepository(dataSource);

        // Act
        var updated = await repository.UpdateTokenAsync(
            NewIdentitySub(),
            NewCiphertext(),
            NewAccessTokenExpiry(),
            NewRefreshTokenExpiry(),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(updated);
    }

    [Fact]
    public async Task UpdateTokenAsync_ReturnsFalse_WhenNoRowMatched()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(0));
        var repository = new PsnLinkRepository(dataSource);

        // Act
        var updated = await repository.UpdateTokenAsync(
            NewIdentitySub(), NewCiphertext(), null, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(updated);
    }

    [Fact]
    public async Task UpdateTokenAsync_PassesDbNullTimestamps_WhenExpiryIsAbsent()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new PsnLinkRepository(dataSource);

        // Act
        await repository.UpdateTokenAsync(
            NewIdentitySub(), NewCiphertext(), null, null, TestContext.Current.CancellationToken);

        // Assert
        var command = dataSource.ExecutedCommands[0];
        Assert.Equal(DBNull.Value, command.Parameters[CuratorSqlParameters.AccessTokenExpiresAt].Value);
        Assert.Equal(DBNull.Value, command.Parameters[CuratorSqlParameters.RefreshTokenExpiresAt].Value);
    }

    [Fact]
    public async Task UpdateTokenAsync_ConvertsUnixSecondsToAUtcTimestamp()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new PsnLinkRepository(dataSource);

        var accessTokenExpiry = NewAccessTokenExpiry();

        // Act
        await repository.UpdateTokenAsync(
            NewIdentitySub(), NewCiphertext(), accessTokenExpiry, null, TestContext.Current.CancellationToken);

        // Assert
        var command = dataSource.ExecutedCommands[0];
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(accessTokenExpiry),
            command.Parameters[CuratorSqlParameters.AccessTokenExpiresAt].Value);
    }
}
