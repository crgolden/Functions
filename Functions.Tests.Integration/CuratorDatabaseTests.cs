namespace Functions.Tests.Integration;

using Npgsql;

[Trait("Category", "Integration")]
public sealed class CuratorDatabaseTests
{
    [Fact]
    public async Task InitializingAgainstADatabaseWithoutTheTestSuffix_IsRefusedBeforeAnythingIsWritten()
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = Generated.NewHostname(),
            Database = Generated.NewDatabaseName(),
        }.ConnectionString;
        await using var database = new CuratorDatabase();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.InitializeFromAsync(connectionString).AsTask());

        Assert.Contains(CuratorTestDatabaseContractConstants.TestDatabaseNameSuffix, refusal.Message, StringComparison.Ordinal);
    }
}
