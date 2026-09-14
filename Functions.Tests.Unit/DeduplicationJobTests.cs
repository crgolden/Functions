namespace Functions.Tests.Unit;

using System.Data;
using Churches.Moderation;
using Microsoft.Azure.Functions.Worker;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class DeduplicationJobTests
{
    private const double IdenticalScore = 1.0;
    private const double NoMatchScore = 0.0;
    private const double NoDistance = 0.0;
    private const int MilePrecision = 1;
    private const double Equator = 0.0;
    private const double PrimeMeridian = 0.0;
    private const double OneDegree = 1.0;
    private const long AtMostOneBucketApart = 1;

    private static readonly double LatitudeCellDegrees =
        DeduplicationJob.MaxDistanceMiles / DeduplicationJob.MilesPerDegreeLatitude;

    [Fact]
    public void JaroWinkler_IdenticalStrings_ReturnsOne()
    {
        // Arrange
        var churchName = TestValues.NewChurchName();

        // Act
        var score = DeduplicationJob.JaroWinkler(churchName, churchName);

        // Assert
        Assert.Equal(IdenticalScore, score);
    }

    [Fact]
    public void JaroWinkler_FirstStringEmpty_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(string.Empty, TestValues.NewChurchName());

        // Assert
        Assert.Equal(NoMatchScore, score);
    }

    [Fact]
    public void JaroWinkler_SecondStringEmpty_ReturnsZero()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(TestValues.NewChurchName(), string.Empty);

        // Assert
        Assert.Equal(NoMatchScore, score);
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
        Assert.Equal(NoMatchScore, score);
    }

    [Fact]
    public void JaroWinkler_TranspositionCase_MatchesReference()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(
            JaroWinklerPublishedExampleConstants.MarthaName,
            JaroWinklerPublishedExampleConstants.MarthaNameTransposed);

        // Assert
        Assert.Equal(
            JaroWinklerPublishedExampleConstants.MarthaScore,
            score,
            JaroWinklerPublishedExampleConstants.PublishedDecimalPlaces);
    }

    [Fact]
    public void JaroWinkler_PartialMatchWithPrefix_MatchesReference()
    {
        // Act
        var score = DeduplicationJob.JaroWinkler(
            JaroWinklerPublishedExampleConstants.DixonName,
            JaroWinklerPublishedExampleConstants.DixonNameExpanded);

        // Assert
        Assert.Equal(
            JaroWinklerPublishedExampleConstants.DixonScore,
            score,
            JaroWinklerPublishedExampleConstants.PublishedDecimalPlaces);
    }

    [Fact]
    public void JaroWinkler_RepeatedCharsHitAlreadyMatchedSkip_ScoresTheSwapAsOneTransposedPair()
    {
        // Arrange
        var firstLetter = TestValues.NewTokenFromFirstHalfOfAlphabet(1);
        var secondLetter = TestValues.NewTokenFromSecondHalfOfAlphabet(1);
        var sharedPrefix = $"{firstLetter}{secondLetter}";
        var repeatedThenReversed = $"{sharedPrefix}{secondLetter}{firstLetter}";
        var repeatedInOrder = $"{sharedPrefix}{firstLetter}{secondLetter}";
        var expectedScore = ExpectedJaroWinkler(
            repeatedThenReversed.Length,
            repeatedInOrder.Length,
            matches: repeatedThenReversed.Length,
            transposedPairs: 1,
            sharedPrefixLength: sharedPrefix.Length);

        // Act
        var score = DeduplicationJob.JaroWinkler(repeatedThenReversed, repeatedInOrder);

        // Assert
        Assert.Equal(expectedScore, score);
    }

    [Fact]
    public void JaroWinkler_NoCommonPrefix_NoPrefixBoost()
    {
        // Arrange
        var sharedDigits = TestValues.NewDigitsOnlyToken();
        var firstLead = TestValues.NewTokenFromFirstHalfOfAlphabet(1);
        var secondLead = TestValues.NewTokenFromSecondHalfOfAlphabet(1);
        var firstName = $"{firstLead}{sharedDigits}";
        var secondName = $"{secondLead}{sharedDigits}";
        var expectedScore = ExpectedJaroWinkler(
            firstName.Length,
            secondName.Length,
            matches: sharedDigits.Length,
            transposedPairs: 0,
            sharedPrefixLength: 0);

        // Act
        var score = DeduplicationJob.JaroWinkler(firstName, secondName);

        // Assert
        Assert.Equal(expectedScore, score);
    }

    [Fact]
    public void JaroWinkler_SharedPrefixLongerThanTheWindow_CapsTheBoost()
    {
        // Arrange
        var sharedPrefix = TestValues.NewLettersOnlyToken();
        var firstTail = TestValues.NewTokenFromFirstHalfOfAlphabet(6);
        var secondTail = TestValues.NewTokenFromSecondHalfOfAlphabet(6);
        var firstName = $"{sharedPrefix}{firstTail}";
        var secondName = $"{sharedPrefix}{secondTail}";
        var expectedScore = ExpectedJaroWinkler(
            firstName.Length,
            secondName.Length,
            matches: sharedPrefix.Length,
            transposedPairs: 0,
            sharedPrefixLength: sharedPrefix.Length);

        // Act
        var score = DeduplicationJob.JaroWinkler(firstName, secondName);

        // Assert
        Assert.True(sharedPrefix.Length > DeduplicationJob.WinklerMaxPrefixLength);
        Assert.Equal(expectedScore, score);
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
        Assert.Equal(NoDistance, miles);
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
        var candidateQueryCommandText = connection.ExecutedCommands[0].CommandText;
        Assert.Contains("NOT ([Latitude] = 0 AND [Longitude] = 0)", candidateQueryCommandText, StringComparison.Ordinal);
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
        var candidateQueryCommandText = connection.ExecutedCommands[0].CommandText;
        Assert.Contains("NOT LIKE 'PO BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P O BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O. BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'P.O BOX%'", candidateQueryCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ManyChurchesShareOneBucket_CompletesAndMatchesOnlySimilarNames()
    {
        // Arrange
        var sharedLatitude = TestValues.NewScoredLatitude();
        var sharedLongitude = TestValues.NewScoredLongitude();
        var churchesPerGroup = TestValues.NewChurchCountSharingABucket();
        var table = BuildChurchTable();
        AddChurchRows(
            table, churchesPerGroup, TestValues.NewChurchName(), sharedLatitude, sharedLongitude);
        AddChurchRows(
            table, churchesPerGroup, TestValues.NewDigitsOnlyName(), sharedLatitude, sharedLongitude);

        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var insertCount = connection.ExecutedCommands.Count(
            c => c.CommandText.Contains("INSERT INTO [dbo].[UserCorrections]", StringComparison.Ordinal));
        Assert.Equal(PairsAmong(churchesPerGroup) + PairsAmong(churchesPerGroup), insertCount);
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
            TestValues.WithATrailingLetter(churchName),
            sharedLatitude,
            sharedLongitude);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            connection.ExecutedCommands,
            candidateQuery => Assert.Contains("FROM [dbo].[Churches]", candidateQuery.CommandText, StringComparison.Ordinal),
            suggestionWrite => Assert.Contains("INSERT INTO [dbo].[UserCorrections]", suggestionWrite.CommandText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_ClosePairStraddlingBucketBoundary_StillWritesSuggestion()
    {
        // Arrange
        var churchName = TestValues.NewChurchName();
        var boundaryOffset = TestValues.NewOffsetWithinHalfACell();
        var justInsideACellBoundary = LatitudeCellDegrees * (1 - boundaryOffset);
        var justPastACellBoundary = LatitudeCellDegrees * (1 + boundaryOffset);
        var lowerBucketChurchId = Guid.NewGuid();
        var upperBucketChurchId = Guid.NewGuid();
        var table = BuildChurchTable();
        table.Rows.Add(lowerBucketChurchId, churchName, Equator, justInsideACellBoundary);
        table.Rows.Add(
            upperBucketChurchId,
            TestValues.WithATrailingLetter(churchName),
            Equator,
            justPastACellBoundary);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            connection.ExecutedCommands,
            candidateQuery => Assert.Contains("FROM [dbo].[Churches]", candidateQuery.CommandText, StringComparison.Ordinal),
            suggestionWrite => Assert.Contains("INSERT INTO [dbo].[UserCorrections]", suggestionWrite.CommandText, StringComparison.Ordinal));
    }

    [Fact]
    public void BucketKey_PointsWithinCellSize_MapToSameOrAdjacentBuckets()
    {
        // Arrange
        var boundaryOffset = TestValues.NewOffsetWithinHalfACell();
        var justInsideACellBoundary = LatitudeCellDegrees * (1 - boundaryOffset);
        var justPastACellBoundary = LatitudeCellDegrees * (1 + boundaryOffset);

        // Act
        var keyA = DeduplicationJob.BucketKey(
            Equator, justInsideACellBoundary, LatitudeCellDegrees, LatitudeCellDegrees);
        var keyB = DeduplicationJob.BucketKey(
            Equator, justPastACellBoundary, LatitudeCellDegrees, LatitudeCellDegrees);

        // Assert
        Assert.True(Math.Abs(keyA.LatBucket - keyB.LatBucket) <= AtMostOneBucketApart);
        Assert.True(Math.Abs(keyA.LonBucket - keyB.LonBucket) <= AtMostOneBucketApart);
    }

    private static double ExpectedJaroWinkler(
        int firstLength, int secondLength, int matches, int transposedPairs, int sharedPrefixLength)
    {
        double[] ratios =
        [
            (double)matches / firstLength,
            (double)matches / secondLength,
            (double)(matches - transposedPairs) / matches,
        ];
        var jaro = ratios.Sum() / ratios.Length;
        var boostedPrefixLength = Math.Min(sharedPrefixLength, DeduplicationJob.WinklerMaxPrefixLength);
        return jaro + (boostedPrefixLength * DeduplicationJob.WinklerPrefixScale * (1 - jaro));
    }

    private static int PairsAmong(int count) => Enumerable.Range(0, count).Sum();

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
