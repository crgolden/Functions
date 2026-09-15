namespace Functions.Tests.Unit;

using System.Data;
using Churches;
using Churches.Geocoding;
using Microsoft.Extensions.Configuration;
using TestSupport;
using static TestSupport.TestValues;

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
            NewChurchId(),
            TestValues.NewStreet(),
            streetedChurchCity,
            TestValues.NewStateCode(),
            TestValues.NewZip());
        table.Rows.Add(
            NewChurchId(),
            DBNull.Value,
            TestValues.NewCity(),
            TestValues.NewStateCode(),
            TestValues.NewZip());

        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = NewJob(connection);

        // Act
        var result = await job.LoadZeroCoordChurchesAsync(NewReGeocodeBatchSize(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            result,
            streetedChurch => Assert.Equal(streetedChurchCity, streetedChurch.City),
            streetlessChurch => Assert.Null(streetlessChurch.Street));
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
        await job.LoadZeroCoordChurchesAsync(NewReGeocodeBatchSize(), TestContext.Current.CancellationToken);

        // Assert
        var candidateQueryCommandText = connection.ExecutedCommands[0].CommandText;
        Assert.Contains("NOT LIKE 'PO BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P O BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O. BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
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
        var censusGeocoderUrl = TestValues.NewProviderBaseAddress().ToString();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new(ChurchSettingKeys.CensusGeocoderUrl, censusGeocoderUrl)])
            .Build();
        var writer = new ChurchWriter(connection, FakeServiceBus.CreateSenders().Senders);
        return new ReGeocodeJob(new StubHttpClientFactory(), writer, connection, config);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
