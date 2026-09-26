namespace Functions.Tests.Unit;

using System.Data;
using Functions.Curator.Enrichment;
using Functions.Tests.Unit.TestSupport;

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
            Generated.NewIdentitySub(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(rawg);
        Assert.Null(opencritic);
    }

    [Fact]
    public async Task GetDecryptedKeyMaterialAsync_ReturnsBothKeys_WhenBothConfigured()
    {
        // Arrange
        var rawgKeyMaterial = Generated.NewCiphertext();
        var openCriticKeyMaterial = Generated.NewCiphertext();
        var table = FakeResultSet.WithColumns(
            typeof(byte[]),
            typeof(byte[]));
        table.Rows.Add(rawgKeyMaterial, openCriticKeyMaterial);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentKeysRepository(dataSource);

        // Act
        var (rawg, opencritic) = await repository.GetDecryptedKeyMaterialAsync(
            Generated.NewIdentitySub(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rawgKeyMaterial, rawg);
        Assert.Equal(openCriticKeyMaterial, opencritic);
    }

    [Fact]
    public async Task GetDecryptedKeyMaterialAsync_ReturnsNullForTheColumnTheUserNeverConfigured()
    {
        // Arrange
        var rawgKeyMaterial = Generated.NewCiphertext();
        var table = FakeResultSet.WithColumns(
            typeof(byte[]),
            typeof(byte[]));
        table.Rows.Add(rawgKeyMaterial, DBNull.Value);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentKeysRepository(dataSource);

        // Act
        var (rawg, opencritic) = await repository.GetDecryptedKeyMaterialAsync(
            Generated.NewIdentitySub(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rawgKeyMaterial, rawg);
        Assert.Null(opencritic);
    }

    [Fact]
    public async Task MarkRawgKeyRejectedAsync_UpdatesTheRawgRejectionColumn()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new EnrichmentKeysRepository(dataSource);

        // Act
        await repository.MarkRawgKeyRejectedAsync(Generated.NewIdentitySub(), TestContext.Current.CancellationToken);

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
            Generated.NewIdentitySub(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            "opencritic_key_rejected_at",
            dataSource.ExecutedCommands[0].CapturedCommandText,
            StringComparison.Ordinal);
    }
}
