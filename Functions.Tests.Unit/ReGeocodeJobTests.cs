namespace Functions.Tests.Unit;

using System.Data;
using Functions.Churches;
using Functions.Churches.Geocoding;
using Functions.Tests.Unit.TestSupport;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class ReGeocodeJobTests
{
    [Fact]
    public async Task LoadZeroCoordChurchesAsync_MapsRowsAndNullStreet()
    {
        // Arrange
        var streetedChurchCity = Generated.NewCity();
        var table = ZeroCoordLocationTable();
        table.Rows.Add(
            NewChurchId(),
            Generated.NewStreet(),
            streetedChurchCity,
            Generated.NewStateCodeText(),
            Generated.NewZip());
        table.Rows.Add(
            NewChurchId(),
            DBNull.Value,
            Generated.NewCity(),
            Generated.NewStateCodeText(),
            Generated.NewZip());

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
        connection.Enqueue(FakeDbCommand.WithReader(ZeroCoordLocationTable()));
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

    [Fact]
    public async Task LoadZeroCoordCampusesAsync_MapsRowsAndNullStreet()
    {
        // Arrange
        var streetedCampusCity = Generated.NewCity();
        var table = ZeroCoordLocationTable();
        table.Rows.Add(
            NewCampusId(),
            Generated.NewStreet(),
            streetedCampusCity,
            Generated.NewStateCodeText(),
            Generated.NewZip());
        table.Rows.Add(
            NewCampusId(),
            DBNull.Value,
            Generated.NewCity(),
            Generated.NewStateCodeText(),
            Generated.NewZip());

        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = NewJob(connection);

        // Act
        var result = await job.LoadZeroCoordCampusesAsync(NewReGeocodeBatchSize(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            result,
            streetedCampus => Assert.Equal(streetedCampusCity, streetedCampus.City),
            streetlessCampus => Assert.Null(streetlessCampus.Street));
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("FROM [dbo].[Campuses]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadZeroCoordCampusesAsync_QueryExcludesPoBoxAddressesAndCampusesOfInactiveChurches()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ZeroCoordLocationTable()));
        var job = NewJob(connection);

        // Act
        await job.LoadZeroCoordCampusesAsync(NewReGeocodeBatchSize(), TestContext.Current.CancellationToken);

        // Assert
        var candidateQueryCommandText = connection.ExecutedCommands[0].CommandText;
        Assert.Contains("cm.[Street] NOT LIKE 'PO BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("cm.[Street] NOT LIKE 'P O BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("cm.[Street] NOT LIKE 'P.O. BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("cm.[Street] NOT LIKE 'P.O BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN [dbo].[Churches] ch ON ch.[Id] = cm.[ChurchId]", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("ch.[IsActive] = 1", candidateQueryCommandText, StringComparison.Ordinal);
    }

    private static DataTable ZeroCoordLocationTable()
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string));
        return table;
    }

    private static ReGeocodeJob NewJob(FakeDbConnection connection)
    {
        var telemetry = TelemetryHarness.Shared.Telemetry;
        var writer = new ChurchWriter(connection, FakeServiceBus.CreateSenders().Senders);
        return new ReGeocodeJob(
            new CensusGeocoder(new StubHttpClientFactory(), Generated.NewProviderBaseAddress(), telemetry),
            writer,
            connection,
            telemetry);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
