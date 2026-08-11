namespace Functions.Tests.Unit;

using System.Data;
using System.Linq;
using Microsoft.Azure.Functions.Worker;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class DeduplicationJobTests
{
    [Fact]
    public void JaroWinkler_IdenticalStrings_ReturnsOne()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler("grace", "grace");

        // Assert
        Assert.Equal(1.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_FirstStringEmpty_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(string.Empty, "grace");

        // Assert
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_SecondStringEmpty_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler("grace", string.Empty);

        // Assert
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_NoCommonCharacters_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler("abc", "xyz");

        // Assert
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_TranspositionCase_MatchesReference()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler("martha", "marhta");

        // Assert
        Assert.Equal(0.961, score, 3);
    }

    [Fact]
    public void JaroWinkler_PartialMatchWithPrefix_MatchesReference()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler("dixon", "dicksonx");

        // Assert
        Assert.Equal(0.813, score, 3);
    }

    [Fact]
    public void JaroWinkler_RepeatedCharsHitAlreadyMatchedSkip_HighSimilarity()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler("abba", "abab");

        // Assert
        Assert.InRange(score, 0.9, 1.0);
    }

    [Fact]
    public void JaroWinkler_NoCommonPrefix_NoPrefixBoost()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler("abc", "xbc");

        // Assert
        Assert.Equal(0.778, score, 3);
    }

    [Fact]
    public void JaroWinkler_FullPrefixWindow_CappedBoost()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler("abcdef", "abcdxy");

        // Assert
        Assert.Equal(0.867, score, 3);
    }

    [Fact]
    public void HaversineDistance_OneDegreeLongitudeAtEquator_MatchesGreatCircle()
    {
        // Act
        var miles = DeduplicationJob.HaversineDistance(0.0, 0.0, 0.0, 1.0);

        // Assert
        Assert.Equal(69.1, miles, 1);
    }

    [Fact]
    public void HaversineDistance_IdenticalCoordinates_ReturnsZero()
    {
        // Act
        var miles = DeduplicationJob.HaversineDistance(0.0, 0.0, 0.0, 0.0);

        // Assert
        Assert.Equal(0.0, miles, 5);
    }

    [Fact]
    public async Task Run_ConnectionClosedNoRows_OpensAndWritesNoSuggestions()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildChurchTable()));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Single(connection.ExecutedCommands);
    }

    [Fact]
    public async Task Run_QueryExcludesZeroCoordinateChurches()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildChurchTable()));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            "NOT ([Latitude] = 0 AND [Longitude] = 0)",
            connection.ExecutedCommands[0].CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_QueryExcludesPoBoxAddresses()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildChurchTable()));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var commandText = connection.ExecutedCommands[0].CommandText;
        Assert.Contains("NOT LIKE 'PO BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P O BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O. BOX%'", commandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O BOX%'", commandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ManyChurchesShareOneBucket_CompletesAndMatchesOnlySimilarNames()
    {
        // Arrange
        const int perGroup = 20;
        var table = BuildChurchTable();
        for (var i = 0; i < perGroup; i++)
        {
            table.Rows.Add(Guid.NewGuid(), "Grace Church", 40.0, -105.0);
        }

        for (var i = 0; i < perGroup; i++)
        {
            table.Rows.Add(Guid.NewGuid(), "Unrelated Store", 40.0, -105.0);
        }

        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        const int expectedInsertsPerGroup = (perGroup * (perGroup - 1)) / 2;
        var insertCount = connection.ExecutedCommands.Count(
            c => c.CommandText.Contains("INSERT INTO [dbo].[UserCorrections]", StringComparison.Ordinal));
        Assert.Equal(expectedInsertsPerGroup * 2, insertCount);
    }

    [Fact]
    public async Task Run_TwoChurchesFarApart_SkipsOnDistance()
    {
        // Arrange
        var table = BuildChurchTable();
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 0.0);
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 1.0);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(connection.ExecutedCommands);
    }

    [Fact]
    public async Task Run_TwoChurchesCloseButDissimilarNames_SkipsOnSimilarity()
    {
        // Arrange
        var table = BuildChurchTable();
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 0.0);
        table.Rows.Add(Guid.NewGuid(), "Walmart Store", 0.0, 0.0);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(connection.ExecutedCommands);
    }

    [Fact]
    public async Task Run_TwoChurchesCloseAndSimilar_WritesSuggestion()
    {
        // Arrange
        var table = BuildChurchTable();
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 0.0);
        table.Rows.Add(Guid.NewGuid(), "Grace Churches", 0.0, 0.0);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO [dbo].[UserCorrections]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ClosePairStraddlingBucketBoundary_StillWritesSuggestion()
    {
        // Arrange
        var table = BuildChurchTable();
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 0.0014);
        table.Rows.Add(Guid.NewGuid(), "Grace Churches", 0.0, 0.0016);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO [dbo].[UserCorrections]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void BucketKey_PointsWithinCellSize_MapToSameOrAdjacentBuckets()
    {
        // Act
        const double milesPerDegreeLatitude = 69.1;
        var latCellDegrees = 0.1 / milesPerDegreeLatitude;

        var keyA = DeduplicationJob.BucketKey(0.0, 0.0014, latCellDegrees, latCellDegrees);
        var keyB = DeduplicationJob.BucketKey(0.0, 0.0016, latCellDegrees, latCellDegrees);

        // Assert
        Assert.True(Math.Abs(keyA.LatBucket - keyB.LatBucket) <= 1);
        Assert.True(Math.Abs(keyA.LonBucket - keyB.LonBucket) <= 1);
    }

    private static DataTable BuildChurchTable()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("CanonicalName", typeof(string));
        table.Columns.Add("Latitude", typeof(double));
        table.Columns.Add("Longitude", typeof(double));
        return table;
    }
}