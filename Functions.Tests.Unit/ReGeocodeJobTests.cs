namespace Functions.Tests.Unit;

using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ReGeocodeJobTests
{
    [Fact]
    public async Task LoadZeroCoordChurchesAsync_MapsRowsAndNullStreet()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("Street", typeof(string));
        table.Columns.Add("City", typeof(string));
        table.Columns.Add("State", typeof(string));
        table.Columns.Add("Zip", typeof(string));
        table.Rows.Add(Guid.NewGuid(), "1 Main St", "Denver", "CO", "80201");
        table.Rows.Add(Guid.NewGuid(), DBNull.Value, "Boulder", "CO", "80301");

        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = NewJob(connection);

        // Act
        var result = await job.LoadZeroCoordChurchesAsync(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Denver", result[0].City);
        Assert.Null(result[1].Street);
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("WHERE [Latitude] = 0 AND [Longitude] = 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadZeroCoordChurchesAsync_QueryExcludesPoBoxAddresses()
    {
        // Regression guard: PO Box addresses have no street-level TIGER/Line match, so Census's
        // address geocoder can never resolve them. A production sample found ~77% of zero-coord
        // churches (29,859 of 38,747) were PO Boxes, so a random candidate batch was dominated by
        // permanently-ungeocodable rows, starving real street addresses of retry budget. The
        // SQL-level exclusion is the actual fix; this asserts it stays in place.
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var job = NewJob(connection);

        await job.LoadZeroCoordChurchesAsync(100, TestContext.Current.CancellationToken);

        var commandText = connection.ExecutedCommands[0].CommandText;
        Assert.Contains("NOT LIKE 'PO BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P O BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O. BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O BOX%'", commandText, StringComparison.Ordinal);
    }

    private static ReGeocodeJob NewJob(FakeDbConnection connection)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new("CensusGeocoderUrl", "https://example/geocode")])
            .Build();
        var writer = new ChurchWriter(connection, FakeServiceBus.Create().Factory);
        return new ReGeocodeJob(new StubHttpClientFactory(), writer, connection, config, NullLogger<ReGeocodeJob>.Instance);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
