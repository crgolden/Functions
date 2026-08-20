namespace Functions.Tests.Unit;

using System.Data;
using System.Text.Json;
using Curator.Jobs;
using Curator.Library;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class JobRunsRepositoryTests
{
    [Fact]
    public async Task ReapExpiredLeasesAsync_ReturnsEveryReapedRunId()
    {
        // Arrange
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ExpiredLeaseReaperTests.RunIdTable(first, second)));
        var repository = new JobRunsRepository(dataSource);

        // Act
        var reaped = await repository.ReapExpiredLeasesAsync(
            "abandoned",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([first.ToString(), second.ToString()], reaped);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_ReturnsNothing_WhenNoRunWasAbandoned()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ExpiredLeaseReaperTests.RunIdTable()));
        var repository = new JobRunsRepository(dataSource);

        // Act
        var reaped = await repository.ReapExpiredLeasesAsync(
            "abandoned",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(reaped);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_TargetsOnlyRunningRowsWhoseLeaseIsLapsedOrAbsent()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ExpiredLeaseReaperTests.RunIdTable()));
        var repository = new JobRunsRepository(dataSource);

        // Act
        await repository.ReapExpiredLeasesAsync(
            "abandoned",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].CapturedCommandText;
        Assert.Contains("WHERE status = 'running' AND (lease_expires_at IS NULL OR lease_expires_at <= now())", sql);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNoRunExists()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(RunTable()));
        var repository = new JobRunsRepository(dataSource);

        // Act
        var run = await repository.GetAsync(Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(run);
    }

    [Fact]
    public async Task GetAsync_ReadsResultSummary_WhenTheRunSucceededWithOne()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var identitySub = Guid.NewGuid();
        var storedSummary = JsonSerializer.Serialize(new LibraryRefreshResultSummary
        {
            RawgEnrichedTitles = ["Bloodborne"],
            OpenCriticEnrichedTitles = [],
            OpenCriticTopupIncomplete = false,
            RejectedProviders = [],
            UnavailableProviders = [],
        });
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(RunTable(
            (runId, "library_refresh", identitySub, "succeeded", null, 1, storedSummary))));
        var repository = new JobRunsRepository(dataSource);

        // Act
        var run = await repository.GetAsync(runId.ToString(), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(run);
        Assert.Equal(storedSummary, run.ResultSummary);
        var roundTripped = JsonSerializer.Deserialize<LibraryRefreshResultSummary>(storedSummary);
        Assert.Equal(["Bloodborne"], roundTripped?.RawgEnrichedTitles);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullResultSummary_WhenTheRunHasNotSucceededYet()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(RunTable(
            (runId, "library_refresh", null, "running", null, 1, null))));
        var repository = new JobRunsRepository(dataSource);

        // Act
        var run = await repository.GetAsync(runId.ToString(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(run?.ResultSummary);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_SparesALapsedRunThatIsStillWithinTheRedeliveryWindow()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ExpiredLeaseReaperTests.RunIdTable()));
        var repository = new JobRunsRepository(dataSource);

        // Act
        await repository.ReapExpiredLeasesAsync(
            "abandoned",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].CapturedCommandText;
        Assert.Contains("updated_at <= now() - make_interval(secs => @abandoned_after_seconds)", sql);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_WaitsAFullDayBeforeCallingARunAbandoned()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ExpiredLeaseReaperTests.RunIdTable()));
        var repository = new JobRunsRepository(dataSource);

        // Act
        await repository.ReapExpiredLeasesAsync(
            "abandoned",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var parameters = dataSource.ExecutedCommands[0].Parameters;
        Assert.Equal(86400.0, parameters["@abandoned_after_seconds"].Value);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_ClearsTheLease_SoAReapedRunIsNotReapedAgain()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ExpiredLeaseReaperTests.RunIdTable()));
        var repository = new JobRunsRepository(dataSource);

        // Act
        await repository.ReapExpiredLeasesAsync(
            "abandoned",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("lease_expires_at = NULL", dataSource.ExecutedCommands[0].CapturedCommandText);
    }

    [Fact]
    public async Task MarkRateLimitedAsync_BumpsSeqAndReturnsItSoTheContinuationCanBeCheckpointed()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(7));
        var repository = new JobRunsRepository(dataSource);

        // Act
        var seq = await repository.MarkRateLimitedAsync(
            Guid.NewGuid().ToString(),
            new { rate_limited_provider = "opencritic" },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(7, seq);
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("seq = seq + 1", sql, StringComparison.Ordinal);
        Assert.Contains("status = 'rate_limited'", sql, StringComparison.Ordinal);
        Assert.Contains("RETURNING seq", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_MarksReapedRunsFailedRatherThanLeavingThemRunning()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ExpiredLeaseReaperTests.RunIdTable()));
        var repository = new JobRunsRepository(dataSource);

        // Act
        await repository.ReapExpiredLeasesAsync(
            "abandoned",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("SET status = 'failed'", dataSource.ExecutedCommands[0].CapturedCommandText);
    }

    private static DataTable RunTable(params (Guid RunId, string Kind, Guid? IdentitySub, string Status, string? Error, int Seq, string? ResultSummary)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("run_id", typeof(Guid));
        table.Columns.Add("kind", typeof(string));
        table.Columns.Add("identity_sub", typeof(Guid));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("error", typeof(string));
        table.Columns.Add("seq", typeof(int));
        table.Columns.Add("result_summary", typeof(string));
        foreach (var row in rows)
        {
            table.Rows.Add(
                row.RunId,
                row.Kind,
                (object?)row.IdentitySub ?? DBNull.Value,
                row.Status,
                (object?)row.Error ?? DBNull.Value,
                row.Seq,
                (object?)row.ResultSummary ?? DBNull.Value);
        }

        return table;
    }
}
