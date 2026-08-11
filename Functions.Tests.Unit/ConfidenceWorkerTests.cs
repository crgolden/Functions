namespace Functions.Tests.Unit;

using System.Data;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ConfidenceWorkerTests
{
    [Fact]
    public async Task RecalculateAsync_ChurchFound_ReadsCountsAndUpdatesScore()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ChurchTable(populated: true)));
        connection.Enqueue(FakeDbCommand.WithScalarResult(5));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.Contains("UPDATE [dbo].[Churches]", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
        Assert.Contains("@Score", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecalculateAsync_ChurchNotFound_DoesNotUpdate()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ChurchTable(populated: false)));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(connection.ExecutedCommands);
    }

    private static DataTable ChurchTable(bool populated)
    {
        var table = new DataTable();
        table.Columns.Add("CanonicalName", typeof(string));
        table.Columns.Add("City", typeof(string));
        table.Columns.Add("State", typeof(string));
        table.Columns.Add("Zip", typeof(string));
        table.Columns.Add("Latitude", typeof(double));
        table.Columns.Add("Longitude", typeof(double));
        table.Columns.Add("PhoneNumber", typeof(string));
        table.Columns.Add("Website", typeof(string));
        table.Columns.Add("EmailAddress", typeof(string));
        table.Columns.Add("DenominationId", typeof(Guid));
        table.Columns.Add("WorshipStyle", typeof(int));
        table.Columns.Add("LastVerifiedAt", typeof(DateTime));
        if (populated)
        {
            table.Rows.Add("Grace", "Phoenix", "AZ", "85001", 33.4, -112.0, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, 2, DBNull.Value);
        }

        return table;
    }
}