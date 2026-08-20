namespace Functions.Tests.Unit;

using System.Data;
using Curator.Psn;
using TestSupport;

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
        var table = new DataTable();
        table.Columns.Add("token_response_enc", typeof(byte[]));
        table.Columns.Add("harvest_trophies", typeof(bool));
        table.Rows.Add(new byte[] { 1, 2, 3 }, true);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new PsnLinkRepository(dataSource);

        // Act
        var link = await repository.GetLinkAsync(
            NewIdentitySub(), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(link);
        Assert.Equal(new byte[] { 1, 2, 3 }, link.TokenResponseEnc);
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
        Assert.Equal(DBNull.Value, command.Parameters["@access_token_expires_at"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["@refresh_token_expires_at"].Value);
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
            command.Parameters["@access_token_expires_at"].Value);
    }

    private static string NewIdentitySub() => Guid.NewGuid().ToString();

    private static byte[] NewCiphertext() => Guid.NewGuid().ToByteArray();

    private static long NewAccessTokenExpiry() => 1_700_000_000 + Random.Shared.Next(1, 100_000);

    private static long NewRefreshTokenExpiry() => 1_800_000_000 + Random.Shared.Next(1, 100_000);
}
