namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ConfidenceWorkerTests
{
    [Fact]
    public async Task RecalculateAsync_ChurchFound_ReadsCountsAndUpdatesScore()
    {
        // Arrange
        var churchId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var attributeCount = Random.Shared.Next(1, 50);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(PopulatedChurchTable()));
        connection.Enqueue(FakeDbCommand.WithScalarResult(attributeCount));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(churchId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.Contains("UPDATE [dbo].[Churches]", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
        Assert.Contains("@Score", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecalculateAsync_ChurchNotFound_DoesNotUpdate()
    {
        // Arrange
        var churchId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(EmptyChurchTable()));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(churchId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(connection.ExecutedCommands);
    }

    private static DataTable EmptyChurchTable()
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
        return table;
    }

    private static DataTable PopulatedChurchTable()
    {
        var table = EmptyChurchTable();
        table.Rows.Add(
            NewChurchName(),
            NewCity(),
            NewStateCode(),
            NewZip(),
            NewLatitude(),
            NewLongitude(),
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            Random.Shared.Next(1, 6),
            DBNull.Value);
        return table;
    }

    private static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    private static string NewChurchName() => $"church{LowercaseToken(12)}";

    private static string NewCity() => $"city{LowercaseToken(12)}";

    private static string NewStateCode() =>
        $"{(char)Random.Shared.Next('A', 'Z' + 1)}{(char)Random.Shared.Next('A', 'Z' + 1)}";

    private static string NewZip() => Random.Shared.Next(10000, 100000).ToString(CultureInfo.InvariantCulture);

    private static double NewLatitude() => Math.Round((Random.Shared.NextDouble() * 40) + 1, 4);

    private static double NewLongitude() => -Math.Round((Random.Shared.NextDouble() * 100) + 1, 4);
}
