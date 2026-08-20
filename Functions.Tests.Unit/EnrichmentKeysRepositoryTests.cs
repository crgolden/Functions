namespace Functions.Tests.Unit;

using System.Data;
using Curator.Enrichment;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentKeysRepositoryTests
{
    [Fact]
    public async Task GetDecryptedKeyMaterialAsync_ReturnsBothNull_WhenNoRowExists()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var repository = new EnrichmentKeysRepository(dataSource);

        // Act
        var (rawg, opencritic) = await repository.GetDecryptedKeyMaterialAsync(
            Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(rawg);
        Assert.Null(opencritic);
    }

    [Fact]
    public async Task GetDecryptedKeyMaterialAsync_ReturnsBothKeys_WhenBothConfigured()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("rawg_api_key_enc", typeof(byte[]));
        table.Columns.Add("opencritic_api_key_enc", typeof(byte[]));
        table.Rows.Add(new byte[] { 1 }, new byte[] { 2 });
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentKeysRepository(dataSource);

        // Act
        var (rawg, opencritic) = await repository.GetDecryptedKeyMaterialAsync(
            Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new byte[] { 1 }, rawg);
        Assert.Equal(new byte[] { 2 }, opencritic);
    }

    [Fact]
    public async Task GetDecryptedKeyMaterialAsync_ReturnsNullForTheColumnTheUserNeverConfigured()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("rawg_api_key_enc", typeof(byte[]));
        table.Columns.Add("opencritic_api_key_enc", typeof(byte[]));
        table.Rows.Add(new byte[] { 1 }, DBNull.Value);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentKeysRepository(dataSource);

        // Act
        var (rawg, opencritic) = await repository.GetDecryptedKeyMaterialAsync(
            Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new byte[] { 1 }, rawg);
        Assert.Null(opencritic);
    }

    [Fact]
    public async Task MarkRawgKeyRejectedAsync_UpdatesTheRawgRejectionColumn()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new EnrichmentKeysRepository(dataSource);

        // Act
        await repository.MarkRawgKeyRejectedAsync(Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            "rawg_key_rejected_at", dataSource.ExecutedCommands[0].CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkOpenCriticKeyRejectedAsync_UpdatesTheOpenCriticRejectionColumn()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new EnrichmentKeysRepository(dataSource);

        // Act
        await repository.MarkOpenCriticKeyRejectedAsync(
            Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            "opencritic_key_rejected_at",
            dataSource.ExecutedCommands[0].CapturedCommandText,
            StringComparison.Ordinal);
    }
}
