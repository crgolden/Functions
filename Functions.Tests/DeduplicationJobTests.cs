namespace Functions.Tests;

using System.Data;
using Microsoft.Azure.Functions.Worker;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class DeduplicationJobTests
{
    // --- JaroWinkler (pure, internal static; published reference values) ---
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
        // Act — length OR, first operand true
        var score = DeduplicationJob.JaroWinkler(string.Empty, "grace");

        // Assert
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_SecondStringEmpty_ReturnsZero()
    {
        // Act — length OR, second operand true (first false)
        var score = DeduplicationJob.JaroWinkler("grace", string.Empty);

        // Assert
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_NoCommonCharacters_ReturnsZero()
    {
        // Act — matches == 0
        var score = DeduplicationJob.JaroWinkler("abc", "xyz");

        // Assert
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void JaroWinkler_TranspositionCase_MatchesReference()
    {
        // Act — the canonical MARTHA/MARHTA transposition example
        var score = DeduplicationJob.JaroWinkler("martha", "marhta");

        // Assert
        Assert.Equal(0.961, score, 3);
    }

    [Fact]
    public void JaroWinkler_PartialMatchWithPrefix_MatchesReference()
    {
        // Act — the canonical DIXON/DICKSONX example (matches, prefix boost, no transposition)
        var score = DeduplicationJob.JaroWinkler("dixon", "dicksonx");

        // Assert
        Assert.Equal(0.813, score, 3);
    }

    [Fact]
    public void JaroWinkler_RepeatedCharsHitAlreadyMatchedSkip_HighSimilarity()
    {
        // Act — anagram-ish input exercises the inner-loop `s2Matches[j]` already-true continue
        var score = DeduplicationJob.JaroWinkler("abba", "abab");

        // Assert
        Assert.InRange(score, 0.9, 1.0);
    }

    [Fact]
    public void JaroWinkler_NoCommonPrefix_NoPrefixBoost()
    {
        // Act — first chars differ, so the prefix loop breaks immediately (prefix 0, score == jaro)
        var score = DeduplicationJob.JaroWinkler("abc", "xbc");

        // Assert
        Assert.Equal(0.778, score, 3);
    }

    [Fact]
    public void JaroWinkler_FullPrefixWindow_CappedBoost()
    {
        // Act — four leading chars match, so the prefix loop runs the full (capped at 4) window
        var score = DeduplicationJob.JaroWinkler("abcdef", "abcdxy");

        // Assert
        Assert.Equal(0.867, score, 3);
    }

    // --- HaversineDistance + ToRad (pure, internal static) ---
    [Fact]
    public void HaversineDistance_OneDegreeLongitudeAtEquator_MatchesGreatCircle()
    {
        // Act — one degree of longitude at the equator is ~69.1 statute miles
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

    // --- Run orchestration (FakeDbConnection → DataTable reader) ---
    [Fact]
    public async Task Run_ConnectionClosedNoRows_OpensAndWritesNoSuggestions()
    {
        // Arrange — connection starts Closed; the reader yields zero churches
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildChurchTable()));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert — opened, only the SELECT ran, no INSERT into UserCorrections
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Single(connection.ExecutedCommands);
    }

    [Fact]
    public async Task Run_TwoChurchesFarApart_SkipsOnDistance()
    {
        // Arrange — same name but ~69 miles apart, so the distance guard short-circuits before similarity
        var table = BuildChurchTable();
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 0.0);
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 1.0);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert — no suggestion written
        Assert.Single(connection.ExecutedCommands);
    }

    [Fact]
    public async Task Run_TwoChurchesCloseButDissimilarNames_SkipsOnSimilarity()
    {
        // Arrange — co-located but unrelated names, so the similarity guard short-circuits
        var table = BuildChurchTable();
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 0.0);
        table.Rows.Add(Guid.NewGuid(), "Walmart Store", 0.0, 0.0);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert — no suggestion written
        Assert.Single(connection.ExecutedCommands);
    }

    [Fact]
    public async Task Run_TwoChurchesCloseAndSimilar_WritesSuggestion()
    {
        // Arrange — co-located near-duplicate names clear both guards → WriteSuggestionAsync
        var table = BuildChurchTable();
        table.Rows.Add(Guid.NewGuid(), "Grace Church", 0.0, 0.0);
        table.Rows.Add(Guid.NewGuid(), "Grace Churches", 0.0, 0.0);
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(table));
        var job = new DeduplicationJob(connection);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert — SELECT + INSERT into UserCorrections
        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO [dbo].[UserCorrections]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
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
