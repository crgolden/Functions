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
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
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
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
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
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].CapturedCommandText;
        Assert.Contains("WHERE status = 'running' AND (lease_expires_at IS NULL OR lease_expires_at <= now())", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_ExcludesCancelledFromTheClaim_SoARedeliveryCannotResurrectOne()
    {
        // Arrange
        var cancelledRunId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        var repository = new JobRunsRepository(dataSource);

        // Act
        await repository.TryBeginDeliveryAsync(
            cancelledRunId.ToString(), 0, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].CapturedCommandText;

        Assert.Contains("status NOT IN ('succeeded', 'failed', 'cancelled')", sql, StringComparison.Ordinal);
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
        var enrichedTitle = TestValues.NewGameTitle();
        var storedSummary = JsonSerializer.Serialize(new LibraryRefreshResultSummary
        {
            RawgEnrichedTitles = [enrichedTitle],
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
        Assert.Equal([enrichedTitle], roundTripped?.RawgEnrichedTitles);
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
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].CapturedCommandText;
        Assert.Contains("updated_at <= now() - make_interval(secs => @abandoned_after_seconds)", sql, StringComparison.Ordinal);
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
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
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
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("lease_expires_at = NULL", dataSource.ExecutedCommands[0].CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryMarkRateLimitedAsync_BumpsSeqAndReturnsItSoTheContinuationCanBeCheckpointed()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(7));
        var repository = new JobRunsRepository(dataSource);

        // Act
        var seq = await repository.TryMarkRateLimitedAsync(
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

    [Theory]
    [InlineData(JobRunStatuses.Succeeded)]
    [InlineData(JobRunStatuses.Failed)]
    [InlineData(JobRunStatuses.RateLimited)]
    public async Task MarkingARunTerminal_RequiresItToStillBeRunning_SoACancelSurvivesTheInFlightWorker(
        string terminalStatus)
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new JobRunsRepository(dataSource);
        var runId = Guid.NewGuid().ToString();

        // Act
        await MarkTerminalAsync(repository, terminalStatus, runId);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("AND status = 'running'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryMarkRateLimitedAsync_WhenTheRunIsNoLongerRunning_ReturnsNoSeqRatherThanThrowing()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new JobRunsRepository(dataSource);

        // Act
        var seq = await repository.TryMarkRateLimitedAsync(
            Guid.NewGuid().ToString(),
            new { rate_limited_provider = "opencritic" },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(seq);
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
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("SET status = 'failed'", dataSource.ExecutedCommands[0].CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_WritesTheErrorCodeAlongsideTheProse_SoAReapIsNotJustAnUncodedFailure()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ExpiredLeaseReaperTests.RunIdTable()));
        var repository = new JobRunsRepository(dataSource);

        // Act
        await repository.ReapExpiredLeasesAsync(
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].CapturedCommandText;
        var parameters = dataSource.ExecutedCommands[0].Parameters;

        Assert.Contains("error = @error, error_code = @error_code", sql, StringComparison.Ordinal);
        Assert.Equal(JobErrorCodes.Abandoned, parameters["@error_code"].Value);
    }

    private static async Task MarkTerminalAsync(JobRunsRepository repository, string terminalStatus, string runId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (string.Equals(terminalStatus, JobRunStatuses.Succeeded, StringComparison.Ordinal))
        {
            await repository.TryMarkSucceededAsync(runId, null, cancellationToken);
        }
        else if (string.Equals(terminalStatus, JobRunStatuses.Failed, StringComparison.Ordinal))
        {
            await repository.TryMarkFailedAsync(
                runId, new JobFailure(JobErrorCodes.Unexpected, "failed"), cancellationToken);
        }
        else
        {
            await repository.TryMarkRateLimitedAsync(runId, new { stage = "paused" }, cancellationToken);
        }
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
