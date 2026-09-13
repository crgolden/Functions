namespace Functions.Tests.Unit;

using System.Data;
using Churches;
using Churches.Moderation;
using Microsoft.Azure.Functions.Worker;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class DeduplicationJobTests
{
    private const double IdenticalScore = 1.0;
    private const double NoMatchScore = 0.0;
    private const int ScorePrecision = 5;
    private const int ReferencePrecision = 3;
    private const int MilePrecision = 1;
    private const double Equator = 0.0;
    private const double PrimeMeridian = 0.0;
    private const double OneDegree = 1.0;
    private const long AtMostOneBucketApart = 1;
    private const int ChurchesPerGroup = 20;
    private const int GroupCount = 2;
    private const int SelectThenInsert = 2;
    private const double NearlyIdenticalFloor = 0.9;

    private const string TranspositionName = "martha";
    private const string TranspositionNameTransposed = "marhta";
    private const double TranspositionReferenceScore = 0.961;

    private const string PrefixName = "dixon";
    private const string PrefixNameExpanded = "dicksonx";
    private const double PrefixReferenceScore = 0.813;

    private const string RepeatedCharsName = "abba";
    private const string RepeatedCharsNameReordered = "abab";

    private const string NoPrefixName = "abc";
    private const string NoPrefixNameDiffering = "xbc";
    private const double NoPrefixReferenceScore = 0.778;

    private const string FullPrefixName = "abcdef";
    private const string FullPrefixNameDiverging = "abcdxy";
    private const double FullPrefixReferenceScore = 0.867;

    private static readonly double LatitudeCellDegrees =
        DeduplicationJob.MaxDistanceMiles / DeduplicationJob.MilesPerDegreeLatitude;

    private static readonly double JustInsideACellBoundary = LatitudeCellDegrees * 0.95;
    private static readonly double JustPastACellBoundary = LatitudeCellDegrees * 1.05;

    [Fact]
    public void JaroWinkler_IdenticalStrings_ReturnsOne()
    {
        // Arrange
        var churchName = TestValues.NewChurchName();

        // Act
        var score = DeduplicationJob.JaroWinkler(churchName, churchName);

        // Assert
        Assert.Equal(IdenticalScore, score, ScorePrecision);
    }

    [Fact]
    public void JaroWinkler_FirstStringEmpty_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(string.Empty, TestValues.NewChurchName());

        // Assert
        Assert.Equal(NoMatchScore, score, ScorePrecision);
    }

    [Fact]
    public void JaroWinkler_SecondStringEmpty_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(TestValues.NewChurchName(), string.Empty);

        // Assert
        Assert.Equal(NoMatchScore, score, ScorePrecision);
    }

    [Fact]
    public void JaroWinkler_NoCommonCharacters_ReturnsZero()
    {
        // Arrange
        var lettersOnlyName = TestValues.NewLettersOnlyToken();
        var digitsOnlyName = TestValues.NewDigitsOnlyToken();

        // Act
        var score = DeduplicationJob.JaroWinkler(lettersOnlyName, digitsOnlyName);

        // Assert
        Assert.Equal(NoMatchScore, score, ScorePrecision);
    }

    [Fact]
    public void JaroWinkler_TranspositionCase_MatchesReference()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(TranspositionName, TranspositionNameTransposed);

        // Assert
        Assert.Equal(TranspositionReferenceScore, score, ReferencePrecision);
    }

    [Fact]
    public void JaroWinkler_PartialMatchWithPrefix_MatchesReference()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(PrefixName, PrefixNameExpanded);

        // Assert
        Assert.Equal(PrefixReferenceScore, score, ReferencePrecision);
    }

    [Fact]
    public void JaroWinkler_RepeatedCharsHitAlreadyMatchedSkip_HighSimilarity()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(RepeatedCharsName, RepeatedCharsNameReordered);

        // Assert
        Assert.InRange(score, NearlyIdenticalFloor, IdenticalScore);
    }

    [Fact]
    public void JaroWinkler_NoCommonPrefix_NoPrefixBoost()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(NoPrefixName, NoPrefixNameDiffering);

        // Assert
        Assert.Equal(NoPrefixReferenceScore, score, ReferencePrecision);
    }

    [Fact]
    public void JaroWinkler_FullPrefixWindow_CappedBoost()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(FullPrefixName, FullPrefixNameDiverging);

        // Assert
        Assert.Equal(FullPrefixReferenceScore, score, ReferencePrecision);
    }

    [Fact]
    public void HaversineDistance_OneDegreeLongitudeAtEquator_MatchesGreatCircle()
    {
        // Act
        var miles = DeduplicationJob.HaversineDistance(
            Equator, PrimeMeridian, Equator, PrimeMeridian + OneDegree);

        // Assert
        Assert.Equal(DeduplicationJob.MilesPerDegreeLatitude, miles, MilePrecision);
    }

    [Fact]
    public void HaversineDistance_IdenticalCoordinates_ReturnsZero()
    {
        // Arrange
        var latitude = TestValues.NewScoredLatitude();
        var longitude = TestValues.NewScoredLongitude();

        // Act
        var miles = DeduplicationJob.HaversineDistance(latitude, longitude, latitude, longitude);

        // Assert
        Assert.Equal(NoMatchScore, miles, ScorePrecision);
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
        var sharedLatitude = TestValues.NewScoredLatitude();
        var sharedLongitude = TestValues.NewScoredLongitude();
        var table = BuildChurchTable();
        AddChurchRows(
            table, ChurchesPerGroup, TestValues.NewChurchName(), sharedLatitude, sharedLongitude);
        AddChurchRows(
            table, ChurchesPerGroup, TestValues.NewDigitsOnlyName(), sharedLatitude, sharedLongitude);

        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var expectedInsertsPerGroup = ChurchesPerGroup * (ChurchesPerGroup - 1) / GroupCount;
        var insertCount = connection.ExecutedCommands.Count(
            c => c.CommandText.Contains("INSERT INTO [dbo].[UserCorrections]", StringComparison.Ordinal));
        Assert.Equal(expectedInsertsPerGroup * GroupCount, insertCount);
    }

    [Fact]
    public async Task Run_TwoChurchesFarApart_SkipsOnDistance()
    {
        // Arrange
        var churchName = TestValues.NewChurchName();
        var westChurchId = Guid.NewGuid();
        var eastChurchId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(westChurchId, churchName, Equator, PrimeMeridian);
        table.Rows.Add(eastChurchId, churchName, Equator, PrimeMeridian + OneDegree);
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
        var sharedLatitude = TestValues.NewScoredLatitude();
        var sharedLongitude = TestValues.NewScoredLongitude();
        var churchId = Guid.NewGuid();
        var unrelatedBusinessId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(churchId, TestValues.NewChurchName(), sharedLatitude, sharedLongitude);
        table.Rows.Add(
            unrelatedBusinessId, TestValues.NewDigitsOnlyName(), sharedLatitude, sharedLongitude);
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
        var churchName = TestValues.NewChurchName();
        var sharedLatitude = TestValues.NewScoredLatitude();
        var sharedLongitude = TestValues.NewScoredLongitude();
        var originalChurchId = Guid.NewGuid();
        var duplicateChurchId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(originalChurchId, churchName, sharedLatitude, sharedLongitude);
        table.Rows.Add(
            duplicateChurchId,
            TestValues.WithAPluralSuffix(churchName),
            sharedLatitude,
            sharedLongitude);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SelectThenInsert, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO [dbo].[UserCorrections]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ClosePairStraddlingBucketBoundary_StillWritesSuggestion()
    {
        // Arrange
        var churchName = TestValues.NewChurchName();
        var lowerBucketChurchId = Guid.NewGuid();
        var upperBucketChurchId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(lowerBucketChurchId, churchName, Equator, JustInsideACellBoundary);
        table.Rows.Add(
            upperBucketChurchId,
            TestValues.WithAPluralSuffix(churchName),
            Equator,
            JustPastACellBoundary);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SelectThenInsert, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO [dbo].[UserCorrections]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void BucketKey_PointsWithinCellSize_MapToSameOrAdjacentBuckets()
    {
        // Act
        var keyA = DeduplicationJob.BucketKey(
            Equator, JustInsideACellBoundary, LatitudeCellDegrees, LatitudeCellDegrees);
        var keyB = DeduplicationJob.BucketKey(
            Equator, JustPastACellBoundary, LatitudeCellDegrees, LatitudeCellDegrees);

        // Assert
        Assert.True(Math.Abs(keyA.LatBucket - keyB.LatBucket) <= AtMostOneBucketApart);
        Assert.True(Math.Abs(keyA.LonBucket - keyB.LonBucket) <= AtMostOneBucketApart);
    }

    private static void AddChurchRows(DataTable table, int count, string canonicalName, double latitude, double longitude)
    {
        for (var i = 0; i < count; i++)
        {
            var churchId = Guid.NewGuid();
            table.Rows.Add(churchId, canonicalName, latitude, longitude);
        }
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
