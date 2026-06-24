namespace Functions.Tests;

using System.Data;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ConfidenceWorkerTests
{
    [Fact]
    public async Task RecalculateAsync_ChurchFound_ReadsCountsAndUpdatesScore()
    {
        // Arrange — church row + attribute count, then the score UPDATE
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ChurchTable(populated: true)));
        connection.Enqueue(FakeDbCommand.WithScalarResult(5)); // attribute count
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        // Assert — load + count + update; the update writes ConfidenceScore
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.Contains("UPDATE [dbo].[Churches]", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
        Assert.Contains("@Score", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecalculateAsync_ChurchNotFound_DoesNotUpdate()
    {
        // Arrange — empty reader: church does not exist
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ChurchTable(populated: false)));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(Guid.CreateVersion7(DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        // Assert — only the lookup ran; no count query, no update
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
