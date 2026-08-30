namespace Functions.Tests.Integration;

using Functions.Curator.Jobs;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class JobRunsRepositoryTests : IAsyncLifetime
{
    private const string SeedRunSql =
        "INSERT INTO job_runs (run_id, kind, identity_sub, status, seq, updated_at) VALUES ($1, $2, $3, $4, $5, now() + make_interval(secs => $6)) RETURNING run_id";

    private const string SetLeaseSql =
        "UPDATE job_runs SET lease_expires_at = now() + make_interval(secs => $2) WHERE run_id = $1 RETURNING run_id";

    private const string StatusSql = "SELECT status FROM job_runs WHERE run_id = $1";

    private const string SeqSql = "SELECT seq FROM job_runs WHERE run_id = $1";

    private const string ErrorSql = "SELECT error FROM job_runs WHERE run_id = $1";

    private const string ErrorCodeSql = "SELECT error_code FROM job_runs WHERE run_id = $1";

    private const string LeaseSql = "SELECT lease_expires_at FROM job_runs WHERE run_id = $1";

    private const string UpdatedAtSql = "SELECT updated_at FROM job_runs WHERE run_id = $1";

    private const string LeaseIsNullSql = "SELECT lease_expires_at IS NULL FROM job_runs WHERE run_id = $1";

    private const string ErrorIsNullSql = "SELECT error IS NULL FROM job_runs WHERE run_id = $1";

    private const string SummaryIsNullSql = "SELECT result_summary IS NULL FROM job_runs WHERE run_id = $1";

    private const string SummaryNestedSql =
        "SELECT result_summary -> 'nested' ->> 'kept' FROM job_runs WHERE run_id = $1";

    private const string RunExistsSql = "SELECT count(*) FROM job_runs WHERE run_id = $1";

    private readonly CuratorDatabase _database;
    private Guid _identitySub;

    public JobRunsRepositoryTests(CuratorDatabase database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _identitySub = await _database.CreateUserAsync(Token);

    public async ValueTask DisposeAsync() => await _database.DeleteUserAsync(_identitySub, Token);

    [Fact]
    public async Task TryBeginDeliveryAsync_WithTheCurrentSeqOnAQueuedRun_ClaimsItAndTakesALease()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Queued, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var claimed = await repository.TryBeginDeliveryAsync(runId.ToString(), 0, cancellationToken: Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.True(claimed);
        Assert.Equal(JobRunStatuses.Running, status);
        Assert.False(leaseIsNull);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_WithAStaleSeq_ClaimsNothingAndLeavesTheRunQueued()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Queued, 3, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var claimed = await repository.TryBeginDeliveryAsync(runId.ToString(), 2, cancellationToken: Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.False(claimed);
        Assert.Equal(JobRunStatuses.Queued, status);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_WhenTheRunIsRunningUnderALiveLease_DoesNotStealItFromTheLiveWorker()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, 0);
        await SetLeaseAsync(runId, 600);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var claimed = await repository.TryBeginDeliveryAsync(runId.ToString(), 0, cancellationToken: Token);

        // Assert
        Assert.False(claimed);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_WhenTheRunningLeaseHasLapsed_TakesOverTheRow()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, 0);
        await SetLeaseAsync(runId, -600);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var claimed = await repository.TryBeginDeliveryAsync(runId.ToString(), 0, cancellationToken: Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.True(claimed);
        Assert.Equal(JobRunStatuses.Running, status);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_OnATerminalRun_ClaimsNothingEvenWhenTheSeqMatches()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Succeeded, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var claimed = await repository.TryBeginDeliveryAsync(runId.ToString(), 0, cancellationToken: Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.False(claimed);
        Assert.Equal(JobRunStatuses.Succeeded, status);
    }

    [Fact]
    public async Task TryMarkRateLimitedAsync_BumpsTheSeqAndReturnsTheValueTheContinuationMustCarry()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 4, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var newSeq = await repository.TryMarkRateLimitedAsync(runId.ToString(), new { nested = new { kept = "yes" } }, Token);

        // Assert
        var storedSeq = await _database.ScalarAsync<int>(SeqSql, Token, runId);
        var storedNested = await _database.ScalarAsync<string>(SummaryNestedSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.Equal(5, newSeq);
        Assert.Equal(5, storedSeq);
        Assert.Equal("yes", storedNested);
        Assert.True(leaseIsNull);
    }

    [Fact]
    public async Task TryMarkRateLimitedAsync_AfterBumpingTheSeq_MakesTheOldSeqStaleForRedelivery()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);
        await repository.TryMarkRateLimitedAsync(runId.ToString(), new { stage = "paused" }, Token);
        var bumpedSeq = await _database.ScalarAsync<int>(SeqSql, Token, runId);

        // Act
        var staleClaim = await repository.TryBeginDeliveryAsync(runId.ToString(), 0, cancellationToken: Token);
        var currentClaim = await repository.TryBeginDeliveryAsync(
            runId.ToString(), bumpedSeq, cancellationToken: Token);

        // Assert
        Assert.False(staleClaim);
        Assert.True(currentClaim);
    }

    [Fact]
    public async Task RenewLeaseAsync_ExtendsTheLeaseWithoutTouchingUpdatedAt()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, -3600);
        await SetLeaseAsync(runId, 30);
        var leaseBefore = await _database.ScalarAsync<DateTime>(LeaseSql, Token, runId);
        var updatedBefore = await _database.ScalarAsync<DateTime>(UpdatedAtSql, Token, runId);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var renewed = await repository.RenewLeaseAsync(runId.ToString(), cancellationToken: Token);

        // Assert
        var leaseAfter = await _database.ScalarAsync<DateTime>(LeaseSql, Token, runId);
        var updatedAfter = await _database.ScalarAsync<DateTime>(UpdatedAtSql, Token, runId);

        Assert.True(renewed);
        Assert.True(leaseAfter > leaseBefore);
        Assert.Equal(updatedBefore, updatedAfter);
    }

    [Fact]
    public async Task RenewLeaseAsync_OnARunThatIsNotRunning_RenewsNothing()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Queued, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var renewed = await repository.RenewLeaseAsync(runId.ToString(), cancellationToken: Token);

        // Assert
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.False(renewed);
        Assert.True(leaseIsNull);
    }

    [Fact]
    public async Task TryReleaseForRetryAsync_ClearsTheLeaseAndLeavesTheRunRunningSoARedeliveryCanReclaimIt()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 3, 0);
        await SetLeaseAsync(runId, 600);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var released = await repository.TryReleaseForRetryAsync(runId.ToString(), Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);
        var reclaimed = await repository.TryBeginDeliveryAsync(runId.ToString(), 3, cancellationToken: Token);

        Assert.True(released);
        Assert.Equal(JobRunStatuses.Running, status);
        Assert.True(leaseIsNull);
        Assert.True(reclaimed);
    }

    [Fact]
    public async Task TryReleaseForRetryAsync_AdvancesUpdatedAtSoTheReaperDoesNotClaimARunThatIsStillRetrying()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, -3600);
        await SetLeaseAsync(runId, 600);
        var updatedBefore = await _database.ScalarAsync<DateTime>(UpdatedAtSql, Token, runId);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        await repository.TryReleaseForRetryAsync(runId.ToString(), Token);

        // Assert
        var updatedAfter = await _database.ScalarAsync<DateTime>(UpdatedAtSql, Token, runId);

        Assert.True(updatedAfter > updatedBefore);
    }

    [Fact]
    public async Task TryReleaseForRetryAsync_OnARunStoodDownWhileTheWorkerWasBusy_ReleasesNothing()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Cancelled, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var released = await repository.TryReleaseForRetryAsync(runId.ToString(), Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.False(released);
        Assert.Equal(JobRunStatuses.Cancelled, status);
    }

    [Fact]
    public async Task TryMarkSucceededAsync_WritesTheSummaryAsQueryableJsonbAndClearsTheLease()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, 0);
        await SetLeaseAsync(runId, 600);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        await repository.TryMarkSucceededAsync(runId.ToString(), new { nested = new { kept = "yes" } }, Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var storedNested = await _database.ScalarAsync<string>(SummaryNestedSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.Equal(JobRunStatuses.Succeeded, status);
        Assert.Equal("yes", storedNested);
        Assert.True(leaseIsNull);
    }

    [Fact]
    public async Task TryMarkSucceededAsync_WithNoSummary_StoresSqlNullRatherThanTheStringNull()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        await repository.TryMarkSucceededAsync(runId.ToString(), null, Token);

        // Assert
        var summaryIsNull = await _database.ScalarAsync<bool>(SummaryIsNullSql, Token, runId);
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.True(summaryIsNull);
        Assert.Equal(JobRunStatuses.Succeeded, status);
    }

    [Fact]
    public async Task TryMarkFailedAsync_WritesTheStructuredErrorCodeTheSchedulerReads()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);
        var failure = new JobFailure(JobErrorCodes.PsnLinkExpired, "Your PlayStation Network link has expired.");

        // Act
        await repository.TryMarkFailedAsync(runId.ToString(), failure, Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var error = await _database.ScalarAsync<string>(ErrorSql, Token, runId);
        var errorCode = await _database.ScalarAsync<string>(ErrorCodeSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.Equal("failed", status);
        Assert.Equal(failure.Message, error);
        Assert.Equal(JobErrorCodes.PsnLinkExpired, errorCode);
        Assert.True(leaseIsNull);
    }

    [Fact]
    public async Task TryMarkSucceededAsync_OnARunCancelledWhileTheWorkerWasStillBusy_LeavesTheCancelledOutcome()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Cancelled, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var marked = await repository.TryMarkSucceededAsync(runId.ToString(), new { nested = new { kept = "yes" } }, Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var summaryIsNull = await _database.ScalarAsync<bool>(SummaryIsNullSql, Token, runId);

        Assert.False(marked);
        Assert.Equal(JobRunStatuses.Cancelled, status);
        Assert.True(summaryIsNull);
    }

    [Fact]
    public async Task TryMarkFailedAsync_OnARunCancelledWhileTheWorkerWasStillBusy_LeavesTheCancelledOutcome()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Cancelled, 0, 0);
        var repository = new JobRunsRepository(_database.DataSource);
        var failure = new JobFailure(JobErrorCodes.Unexpected, "The job failed unexpectedly.");

        // Act
        var marked = await repository.TryMarkFailedAsync(runId.ToString(), failure, Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var errorIsNull = await _database.ScalarAsync<bool>(ErrorIsNullSql, Token, runId);

        Assert.False(marked);
        Assert.Equal(JobRunStatuses.Cancelled, status);
        Assert.True(errorIsNull);
    }

    [Fact]
    public async Task TryMarkRateLimitedAsync_OnARunCancelledWhileTheWorkerWasStillBusy_BumpsNoSeqSoNoContinuationIsQueued()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Cancelled, 4, 0);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var newSeq = await repository.TryMarkRateLimitedAsync(runId.ToString(), new { stage = "paused" }, Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var storedSeq = await _database.ScalarAsync<int>(SeqSql, Token, runId);

        Assert.Null(newSeq);
        Assert.Equal(JobRunStatuses.Cancelled, status);
        Assert.Equal(4, storedSeq);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_ReapsALapsedRunWhoseUpdatedAtIsOlderThanTheAbandonedWindow()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, -7200);
        await SetLeaseAsync(runId, -600);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var reaped = await repository.ReapExpiredLeasesAsync(
            "Superseded: the processing lease expired.", JobErrorCodes.Abandoned, 3600, Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.Contains(runId.ToString(), reaped);
        Assert.Equal("failed", status);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_LeavesALapsedRunThatWasUpdatedRecently_BecauseThatIsTheRedeliveryWindow()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, 0);
        await SetLeaseAsync(runId, -600);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        var reaped = await repository.ReapExpiredLeasesAsync(
            "Superseded: the processing lease expired.", JobErrorCodes.Abandoned, 3600, Token);

        // Assert
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.DoesNotContain(runId.ToString(), reaped);
        Assert.Equal(JobRunStatuses.Running, status);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_StoresTheAbandonedErrorCode_WhichTheCheckConstraintMustAdmit()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 0, -7200);
        await SetLeaseAsync(runId, -600);
        var repository = new JobRunsRepository(_database.DataSource);

        // Act
        await repository.ReapExpiredLeasesAsync(
            ExpiredLeaseReaper.AbandonedRunError, JobErrorCodes.Abandoned, 3600, Token);

        // Assert
        var errorCode = await _database.ScalarAsync<string>(ErrorCodeSql, Token, runId);
        var error = await _database.ScalarAsync<string>(ErrorSql, Token, runId);

        Assert.Equal(JobErrorCodes.Abandoned, errorCode);
        Assert.Equal(ExpiredLeaseReaper.AbandonedRunError, error);
    }

    [Fact]
    public async Task GetAsync_ForASeededRun_ReadsBackTheKindStatusSeqAndSummary()
    {
        // Arrange
        var runId = await SeedRunAsync(JobRunStatuses.Running, 7, 0);
        var repository = new JobRunsRepository(_database.DataSource);
        await repository.TryMarkSucceededAsync(runId.ToString(), new { nested = new { kept = "yes" } }, Token);

        // Act
        var run = await repository.GetAsync(runId.ToString(), Token);

        // Assert
        Assert.NotNull(run);
        Assert.Equal(JobRunKinds.LibraryRefresh, run.Kind);
        Assert.Equal(JobRunStatuses.Succeeded, run.Status);
        Assert.Equal(7, run.Seq);
        Assert.Equal(_identitySub, run.IdentitySub);
        Assert.Contains("kept", run.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ForAnUnknownRun_ReturnsNull()
    {
        // Arrange
        var repository = new JobRunsRepository(_database.DataSource);
        var unknown = Guid.NewGuid();

        // Act
        var run = await repository.GetAsync(unknown.ToString(), Token);

        // Assert
        var rowCount = await _database.ScalarAsync<long>(RunExistsSql, Token, unknown);

        Assert.Null(run);
        Assert.Equal(0L, rowCount);
    }

    private async Task<Guid> SeedRunAsync(string status, int seq, double updatedAtOffsetSeconds) =>
        await _database.ScalarAsync<Guid>(
            SeedRunSql,
            Token,
            Guid.NewGuid(),
            JobRunKinds.LibraryRefresh,
            _identitySub,
            status,
            seq,
            updatedAtOffsetSeconds);

    private async Task SetLeaseAsync(Guid runId, double leaseOffsetSeconds) =>
        await _database.ScalarAsync<Guid>(SetLeaseSql, Token, runId, leaseOffsetSeconds);
}
