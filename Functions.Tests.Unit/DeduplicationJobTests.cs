namespace Functions.Tests.Unit;

using System.Data;
using Churches;
using Churches.Moderation;
using Microsoft.Azure.Functions.Worker;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class DeduplicationJobTests
{
    private const string LetterAlphabet = "abcdefghijklm";

    private const string DigitAlphabet = "0123456789";

    [Fact]
    public void JaroWinkler_IdenticalStrings_ReturnsOne()
    {
        // Arrange
        var churchName = NewChurchName();

        // Act
        var score = DeduplicationJob.JaroWinkler(churchName, churchName);

        // Assert
        Assert.Equal(1.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_FirstStringEmpty_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(string.Empty, NewChurchName());

        // Assert
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_SecondStringEmpty_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(NewChurchName(), string.Empty);

        // Assert
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_NoCommonCharacters_ReturnsZero()
    {
        // Arrange
        var lettersOnlyName = RandomToken(LetterAlphabet, 8);
        var digitsOnlyName = RandomToken(DigitAlphabet, 8);

        // Act
        var score = DeduplicationJob.JaroWinkler(lettersOnlyName, digitsOnlyName);

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
        // Arrange
        var latitude = NewLatitude();
        var longitude = NewLongitude();

        // Act
        var miles = DeduplicationJob.HaversineDistance(latitude, longitude, latitude, longitude);

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
        var churchesPerGroup = 20;
        var sharedLatitude = NewLatitude();
        var sharedLongitude = NewLongitude();
        var table = BuildChurchTable();
        AddChurchRows(table, churchesPerGroup, NewChurchName(), sharedLatitude, sharedLongitude);
        AddChurchRows(table, churchesPerGroup, NewUnrelatedName(), sharedLatitude, sharedLongitude);

        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var expectedInsertsPerGroup = churchesPerGroup * (churchesPerGroup - 1) / 2;
        var insertCount = connection.ExecutedCommands.Count(
            c => c.CommandText.Contains("INSERT INTO [dbo].[UserCorrections]", StringComparison.Ordinal));
        Assert.Equal(expectedInsertsPerGroup * 2, insertCount);
    }

    [Fact]
    public async Task Run_TwoChurchesFarApart_SkipsOnDistance()
    {
        // Arrange
        var churchName = NewChurchName();
        var westChurchId = Guid.NewGuid();
        var eastChurchId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(westChurchId, churchName, 0.0, 0.0);
        table.Rows.Add(eastChurchId, churchName, 0.0, 1.0);
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
        var sharedLatitude = NewLatitude();
        var sharedLongitude = NewLongitude();
        var churchId = Guid.NewGuid();
        var unrelatedBusinessId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(churchId, NewChurchName(), sharedLatitude, sharedLongitude);
        table.Rows.Add(unrelatedBusinessId, NewUnrelatedName(), sharedLatitude, sharedLongitude);
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
        var churchName = NewChurchName();
        var sharedLatitude = NewLatitude();
        var sharedLongitude = NewLongitude();
        var originalChurchId = Guid.NewGuid();
        var duplicateChurchId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(originalChurchId, churchName, sharedLatitude, sharedLongitude);
        table.Rows.Add(duplicateChurchId, PluralOf(churchName), sharedLatitude, sharedLongitude);
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
        var churchName = NewChurchName();
        var lowerBucketChurchId = Guid.NewGuid();
        var upperBucketChurchId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(lowerBucketChurchId, churchName, 0.0, 0.0014);
        table.Rows.Add(upperBucketChurchId, PluralOf(churchName), 0.0, 0.0016);
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
        // Arrange
        const double milesPerDegreeLatitude = 69.1;
        var latCellDegrees = 0.1 / milesPerDegreeLatitude;

        // Act
        var keyA = DeduplicationJob.BucketKey(0.0, 0.0014, latCellDegrees, latCellDegrees);
        var keyB = DeduplicationJob.BucketKey(0.0, 0.0016, latCellDegrees, latCellDegrees);

        // Assert
        Assert.True(Math.Abs(keyA.LatBucket - keyB.LatBucket) <= 1);
        Assert.True(Math.Abs(keyA.LonBucket - keyB.LonBucket) <= 1);
    }

    private static void AddChurchRows(DataTable table, int count, string canonicalName, double latitude, double longitude)
    {
        for (var i = 0; i < count; i++)
        {
            var churchId = Guid.NewGuid();
            table.Rows.Add(churchId, canonicalName, latitude, longitude);
        }
    }

    private static string RandomToken(string alphabet, int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]));

    private static string NewChurchName() => TestValues.NewChurchName();

    private static string NewUnrelatedName() => RandomToken(DigitAlphabet, 16);

    private static string PluralOf(string name) => name + "s";

    private static double NewLatitude() => Math.Round((Random.Shared.NextDouble() * 40) + 1, 4);

    private static double NewLongitude() => -Math.Round((Random.Shared.NextDouble() * 100) + 1, 4);

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
