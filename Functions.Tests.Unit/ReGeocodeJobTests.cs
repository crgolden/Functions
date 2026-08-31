namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using Churches;
using Churches.Geocoding;
using Microsoft.Extensions.Configuration;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ReGeocodeJobTests
{
    [Fact]
    public async Task LoadZeroCoordChurchesAsync_MapsRowsAndNullStreet()
    {
        // Arrange
        var streetedChurchCity = TestValues.NewCity();
        var table = ZeroCoordChurchTable();
        table.Rows.Add(
            Guid.NewGuid(),
            TestValues.NewStreet(),
            streetedChurchCity,
            TestValues.NewStateCode(),
            TestValues.NewZip());
        table.Rows.Add(
            Guid.NewGuid(),
            DBNull.Value,
            TestValues.NewCity(),
            TestValues.NewStateCode(),
            TestValues.NewZip());

        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = NewJob(connection);

        // Act
        var result = await job.LoadZeroCoordChurchesAsync(NewBatchSize(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(streetedChurchCity, result[0].City);
        Assert.Null(result[1].Street);
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("WHERE [Latitude] = 0 AND [Longitude] = 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadZeroCoordChurchesAsync_QueryExcludesPoBoxAddresses()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ZeroCoordChurchTable()));
        var job = NewJob(connection);

        // Act
        await job.LoadZeroCoordChurchesAsync(NewBatchSize(), TestContext.Current.CancellationToken);

        // Assert
        var commandText = connection.ExecutedCommands[0].CommandText;
        Assert.Contains("NOT LIKE 'PO BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P O BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O. BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O BOX%'", commandText, StringComparison.Ordinal);
    }

    private static DataTable ZeroCoordChurchTable()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("Street", typeof(string));
        table.Columns.Add("City", typeof(string));
        table.Columns.Add("State", typeof(string));
        table.Columns.Add("Zip", typeof(string));
        return table;
    }

    private static ReGeocodeJob NewJob(FakeDbConnection connection)
    {
        var censusGeocoderUrl = $"https://{TestValues.LowercaseToken(12)}.example/geocode";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new(ChurchSettingKeys.CensusGeocoderUrl, censusGeocoderUrl)])
            .Build();
        var writer = new ChurchWriter(connection, FakeServiceBus.Create().Factory);
        return new ReGeocodeJob(new StubHttpClientFactory(), writer, connection, config);
    }

    private static int NewBatchSize() => Random.Shared.Next(10, 500);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
