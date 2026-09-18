namespace Functions.Tests.Unit;

using System.Data;
using Churches.Confidence;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ConfidenceWorkerTests
{
    [Fact]
    public async Task RecalculateAsync_ChurchFound_ReadsTheChurchAndItsAttributeCountInOneQueryThenUpdatesTheScore()
    {
        // Arrange
        var churchId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var attributeCount = Random.Shared.Next(1, 50);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(PopulatedChurchTable(attributeCount)));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(churchId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            connection.ExecutedCommands,
            load => Assert.Contains("FROM [dbo].[ChurchAttributes]", load.CommandText, StringComparison.Ordinal),
            update => Assert.Contains("UPDATE [dbo].[Churches]", update.CommandText, StringComparison.Ordinal));
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

    [Fact]
    public async Task RecalculateAsync_ScoresTheAttributeCountReadAlongsideTheChurch()
    {
        // Arrange
        var churchId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var canonicalName = TestValues.NewChurchName();
        var attributeCount = Random.Shared.Next(1, 50);
        var expectedScore = ConfidenceScoreCalculator.Calculate(
            new ConfidenceInputs(canonicalName, null, null, null, 0, 0, null, null, null, false, 0, null),
            attributeCount);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(SparseChurchTable(canonicalName, DBNull.Value, attributeCount)));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(churchId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedScore, Assert.IsType<decimal>(ScoreUpdate(connection).Parameters["@Score"].Value));
    }

    [Fact]
    public async Task RecalculateAsync_LastVerifiedAtStored_ScoresTheStoredInstantRatherThanNull()
    {
        // Arrange
        var churchId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var canonicalName = TestValues.NewChurchName();
        var lastVerifiedAt = TestValues.NewUtcTimestamp();
        var noAttributes = 0;
        var expectedScore = ConfidenceScoreCalculator.Calculate(
            new ConfidenceInputs(canonicalName, null, null, null, 0, 0, null, null, null, false, 0, lastVerifiedAt),
            noAttributes);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(SparseChurchTable(canonicalName, lastVerifiedAt, noAttributes)));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(churchId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedScore, Assert.IsType<decimal>(ScoreUpdate(connection).Parameters["@Score"].Value));
    }

    [Fact]
    public async Task RecalculateAsync_LastVerifiedAtAbsent_ScoresWithoutTheFreshnessWeight()
    {
        // Arrange
        var churchId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var canonicalName = TestValues.NewChurchName();
        var noAttributes = 0;
        var expectedScore = ConfidenceScoreCalculator.Calculate(
            new ConfidenceInputs(canonicalName, null, null, null, 0, 0, null, null, null, false, 0, null),
            noAttributes);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(SparseChurchTable(canonicalName, DBNull.Value, noAttributes)));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(churchId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedScore, Assert.IsType<decimal>(ScoreUpdate(connection).Parameters["@Score"].Value));
    }

    [Fact]
    public async Task RecalculateAsync_ChurchFound_BindsUpdatedAtAsDateTimeOffset()
    {
        // Arrange
        var churchId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        var attributeCount = Random.Shared.Next(1, 50);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(PopulatedChurchTable(attributeCount)));
        var worker = new ConfidenceWorker(connection);

        // Act
        await worker.RecalculateAsync(churchId, TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<DateTimeOffset>(ScoreUpdate(connection).Parameters["@Now"].Value);
    }

    private static FakeDbCommand ScoreUpdate(FakeDbConnection connection) =>
        connection.ExecutedCommands.Single(command => command.CommandText.Contains("UPDATE [dbo].[Churches]", StringComparison.Ordinal));

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
        table.Columns.Add("LastVerifiedAt", typeof(DateTimeOffset));
        table.Columns.Add("AttributeCount", typeof(int));
        return table;
    }

    private static DataTable SparseChurchTable(string canonicalName, object lastVerifiedAt, int attributeCount)
    {
        var table = EmptyChurchTable();
        table.Rows.Add(
            canonicalName,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            lastVerifiedAt,
            attributeCount);
        return table;
    }

    private static DataTable PopulatedChurchTable(int attributeCount)
    {
        var table = EmptyChurchTable();
        table.Rows.Add(
            TestValues.NewChurchName(),
            TestValues.NewCity(),
            TestValues.NewStateCode(),
            TestValues.NewZip(),
            TestValues.NewScoredLatitude(),
            TestValues.NewScoredLongitude(),
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            TestValues.NewWorshipStyle(),
            DBNull.Value,
            attributeCount);
        return table;
    }
}
