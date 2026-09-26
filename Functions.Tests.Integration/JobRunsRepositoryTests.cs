namespace Functions.Tests.Integration;

using Functions.Curator.Jobs;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class JobRunsRepositoryTests : IAsyncLifetime
{
    private const int SeqOfAFreshRun = 0;
    private const long NoRows = 0L;

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

    private static readonly TimeSpan LiveLease = Generated.NewLiveLease();
    private static readonly TimeSpan LapsedLease = -LiveLease;
    private static readonly TimeSpan AboutToLapseLease = Generated.NewDurationShorterThan(TimeSpan.FromSeconds(JobRunsRepository.DefaultLeaseSeconds));
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan UpdatedJustNow = TimeSpan.Zero;
    private static readonly TimeSpan UpdatedAWhileAgo = -AbandonedAfter;
    private static readonly TimeSpan UpdatedBeyondTheAbandonedWindow = -(AbandonedAfter + LiveLease);

    private readonly CuratorDatabase _database;
    private Guid _identitySub;

    public JobRunsRepositoryTests(CuratorDatabase database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _identitySub = await _database.CreateUserAsync(Token);

    public async ValueTask DisposeAsync() => await _database.DeleteUserAsync(_identitySub, Token);

    [Fact]
    public async Task TryBeginDeliveryAsync_WithTheCurrentSeqOnAQueuedRun_ClaimsItAndTakesALease()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Queued, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);

        var claimed = await repository.TryBeginDeliveryAsync(runId, 0, cancellationToken: Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.True(claimed);
        Assert.Equal(JobRunStatuses.Running, status);
        Assert.False(leaseIsNull);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_WithAStaleSeq_ClaimsNothingAndLeavesTheRunQueued()
    {
        var currentSeq = Generated.NewJobSeq();
        var runId = await SeedRunAsync(JobRunStatuses.Queued, currentSeq, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);

        var claimed = await repository.TryBeginDeliveryAsync(
            runId, currentSeq - 1, cancellationToken: Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.False(claimed);
        Assert.Equal(JobRunStatuses.Queued, status);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_WhenTheRunIsRunningUnderALiveLease_DoesNotStealItFromTheLiveWorker()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedJustNow);
        await SetLeaseAsync(runId, LiveLease);
        var repository = new JobRunsRepository(_database.DataSource);

        var claimed = await repository.TryBeginDeliveryAsync(runId, 0, cancellationToken: Token);

        Assert.False(claimed);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_WhenTheRunningLeaseHasLapsed_TakesOverTheRow()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedJustNow);
        await SetLeaseAsync(runId, LapsedLease);
        var repository = new JobRunsRepository(_database.DataSource);

        var claimed = await repository.TryBeginDeliveryAsync(runId, 0, cancellationToken: Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.True(claimed);
        Assert.Equal(JobRunStatuses.Running, status);
    }

    [Fact]
    public async Task TryBeginDeliveryAsync_OnATerminalRun_ClaimsNothingEvenWhenTheSeqMatches()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Succeeded, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);

        var claimed = await repository.TryBeginDeliveryAsync(runId, 0, cancellationToken: Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.False(claimed);
        Assert.Equal(JobRunStatuses.Succeeded, status);
    }

    [Fact]
    public async Task TryMarkRateLimitedAsync_BumpsTheSeqAndReturnsTheValueTheContinuationMustCarry()
    {
        var seqBefore = Generated.NewJobSeq();
        var runId = await SeedRunAsync(JobRunStatuses.Running, seqBefore, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);
        var keptValue = Generated.NewFieldValue();

        var newSeq = await repository.TryMarkRateLimitedAsync(
            runId, new { nested = new { kept = keptValue } }, Token);

        var storedSeq = await _database.ScalarAsync<int>(SeqSql, Token, runId);
        var storedNested = await _database.ScalarAsync<string>(SummaryNestedSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.Equal(seqBefore + 1, newSeq);
        Assert.Equal(seqBefore + 1, storedSeq);
        Assert.Equal(keptValue, storedNested);
        Assert.True(leaseIsNull);
    }

    [Fact]
    public async Task TryMarkRateLimitedAsync_AfterBumpingTheSeq_MakesTheOldSeqStaleForRedelivery()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);
        await repository.TryMarkRateLimitedAsync(runId, new { stage = Generated.NewFieldValue() }, Token);
        var bumpedSeq = await _database.ScalarAsync<int>(SeqSql, Token, runId);

        var staleClaim = await repository.TryBeginDeliveryAsync(runId, 0, cancellationToken: Token);
        var currentClaim = await repository.TryBeginDeliveryAsync(
            runId, bumpedSeq, cancellationToken: Token);

        Assert.False(staleClaim);
        Assert.True(currentClaim);
    }

    [Fact]
    public async Task RenewLeaseAsync_ExtendsTheLeaseWithoutTouchingUpdatedAt()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedAWhileAgo);
        await SetLeaseAsync(runId, AboutToLapseLease);
        var leaseBefore = await _database.ScalarAsync<DateTime>(LeaseSql, Token, runId);
        var updatedBefore = await _database.ScalarAsync<DateTime>(UpdatedAtSql, Token, runId);
        var repository = new JobRunsRepository(_database.DataSource);

        var renewed = await repository.RenewLeaseAsync(runId, cancellationToken: Token);

        var leaseAfter = await _database.ScalarAsync<DateTime>(LeaseSql, Token, runId);
        var updatedAfter = await _database.ScalarAsync<DateTime>(UpdatedAtSql, Token, runId);

        Assert.True(renewed);
        Assert.True(leaseAfter > leaseBefore);
        Assert.Equal(updatedBefore, updatedAfter);
    }

    [Fact]
    public async Task RenewLeaseAsync_OnARunThatIsNotRunning_RenewsNothing()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Queued, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);

        var renewed = await repository.RenewLeaseAsync(runId, cancellationToken: Token);

        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.False(renewed);
        Assert.True(leaseIsNull);
    }

    [Fact]
    public async Task TryReleaseForRetryAsync_ClearsTheLeaseAndLeavesTheRunRunningSoARedeliveryCanReclaimIt()
    {
        var currentSeq = Generated.NewJobSeq();
        var runId = await SeedRunAsync(JobRunStatuses.Running, currentSeq, UpdatedJustNow);
        await SetLeaseAsync(runId, LiveLease);
        var repository = new JobRunsRepository(_database.DataSource);

        var released = await repository.TryReleaseForRetryAsync(runId, Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);
        var reclaimed = await repository.TryBeginDeliveryAsync(
            runId, currentSeq, cancellationToken: Token);

        Assert.True(released);
        Assert.Equal(JobRunStatuses.Running, status);
        Assert.True(leaseIsNull);
        Assert.True(reclaimed);
    }

    [Fact]
    public async Task TryReleaseForRetryAsync_AdvancesUpdatedAtSoTheReaperDoesNotClaimARunThatIsStillRetrying()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedAWhileAgo);
        await SetLeaseAsync(runId, LiveLease);
        var updatedBefore = await _database.ScalarAsync<DateTime>(UpdatedAtSql, Token, runId);
        var repository = new JobRunsRepository(_database.DataSource);

        await repository.TryReleaseForRetryAsync(runId, Token);

        var updatedAfter = await _database.ScalarAsync<DateTime>(UpdatedAtSql, Token, runId);

        Assert.True(updatedAfter > updatedBefore);
    }

    [Fact]
    public async Task TryReleaseForRetryAsync_OnARunStoodDownWhileTheWorkerWasBusy_ReleasesNothing()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Cancelled, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);

        var released = await repository.TryReleaseForRetryAsync(runId, Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.False(released);
        Assert.Equal(JobRunStatuses.Cancelled, status);
    }

    [Fact]
    public async Task TryMarkSucceededAsync_WritesTheSummaryAsQueryableJsonbAndClearsTheLease()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedJustNow);
        await SetLeaseAsync(runId, LiveLease);
        var repository = new JobRunsRepository(_database.DataSource);
        var keptValue = Generated.NewFieldValue();

        await repository.TryMarkSucceededAsync(
            runId, new { nested = new { kept = keptValue } }, Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var storedNested = await _database.ScalarAsync<string>(SummaryNestedSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.Equal(JobRunStatuses.Succeeded, status);
        Assert.Equal(keptValue, storedNested);
        Assert.True(leaseIsNull);
    }

    [Fact]
    public async Task TryMarkSucceededAsync_WithNoSummary_StoresSqlNullRatherThanTheStringNull()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);

        await repository.TryMarkSucceededAsync(runId, null, Token);

        var summaryIsNull = await _database.ScalarAsync<bool>(SummaryIsNullSql, Token, runId);
        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.True(summaryIsNull);
        Assert.Equal(JobRunStatuses.Succeeded, status);
    }

    [Fact]
    public async Task TryMarkFailedAsync_WritesTheStructuredErrorCodeTheSchedulerReads()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);
        var failure = new JobFailure(JobErrorCodes.PsnLinkExpired, Generated.NewErrorMessage());

        await repository.TryMarkFailedAsync(runId, failure, Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var error = await _database.ScalarAsync<string>(ErrorSql, Token, runId);
        var errorCode = await _database.ScalarAsync<string>(ErrorCodeSql, Token, runId);
        var leaseIsNull = await _database.ScalarAsync<bool>(LeaseIsNullSql, Token, runId);

        Assert.Equal(JobRunStatuses.Failed, status);
        Assert.Equal(failure.Message, error);
        Assert.Equal(JobErrorCodes.PsnLinkExpired, errorCode);
        Assert.True(leaseIsNull);
    }

    [Fact]
    public async Task TryMarkFailedAsync_WritesTheRejectedAppCredentialCode_WhichTheSchemaCheckMustAdmit()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);
        var failure = new JobFailure(JobErrorCodes.PsnCredentialRejected, Generated.NewErrorMessage());

        var marked = await repository.TryMarkFailedAsync(runId, failure, Token);

        var errorCode = await _database.ScalarAsync<string>(ErrorCodeSql, Token, runId);

        Assert.True(marked);
        Assert.Equal(JobErrorCodes.PsnCredentialRejected, errorCode);
    }

    [Fact]
    public async Task TryMarkSucceededAsync_OnARunCancelledWhileTheWorkerWasStillBusy_LeavesTheCancelledOutcome()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Cancelled, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);

        var marked = await repository.TryMarkSucceededAsync(
            runId, new { nested = new { kept = Generated.NewFieldValue() } }, Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var summaryIsNull = await _database.ScalarAsync<bool>(SummaryIsNullSql, Token, runId);

        Assert.False(marked);
        Assert.Equal(JobRunStatuses.Cancelled, status);
        Assert.True(summaryIsNull);
    }

    [Fact]
    public async Task TryMarkFailedAsync_OnARunCancelledWhileTheWorkerWasStillBusy_LeavesTheCancelledOutcome()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Cancelled, SeqOfAFreshRun, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);
        var failure = new JobFailure(JobErrorCodes.Unexpected, Generated.NewErrorMessage());

        var marked = await repository.TryMarkFailedAsync(runId, failure, Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var errorIsNull = await _database.ScalarAsync<bool>(ErrorIsNullSql, Token, runId);

        Assert.False(marked);
        Assert.Equal(JobRunStatuses.Cancelled, status);
        Assert.True(errorIsNull);
    }

    [Fact]
    public async Task TryMarkRateLimitedAsync_OnARunCancelledWhileTheWorkerWasStillBusy_BumpsNoSeqSoNoContinuationIsQueued()
    {
        var seqBefore = Generated.NewJobSeq();
        var runId = await SeedRunAsync(JobRunStatuses.Cancelled, seqBefore, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);

        var newSeq = await repository.TryMarkRateLimitedAsync(
            runId, new { stage = Generated.NewFieldValue() }, Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);
        var storedSeq = await _database.ScalarAsync<int>(SeqSql, Token, runId);

        Assert.Null(newSeq);
        Assert.Equal(JobRunStatuses.Cancelled, status);
        Assert.Equal(seqBefore, storedSeq);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_ReapsALapsedRunWhoseUpdatedAtIsOlderThanTheAbandonedWindow()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedBeyondTheAbandonedWindow);
        await SetLeaseAsync(runId, LapsedLease);
        var repository = new JobRunsRepository(_database.DataSource);

        var reaped = await repository.ReapExpiredLeasesAsync(
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
            AbandonedAfter.TotalSeconds,
            Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.Contains(runId, reaped);
        Assert.Equal(JobRunStatuses.Failed, status);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_LeavesALapsedRunThatWasUpdatedRecently_BecauseThatIsTheRedeliveryWindow()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedJustNow);
        await SetLeaseAsync(runId, LapsedLease);
        var repository = new JobRunsRepository(_database.DataSource);

        var reaped = await repository.ReapExpiredLeasesAsync(
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
            AbandonedAfter.TotalSeconds,
            Token);

        var status = await _database.ScalarAsync<string>(StatusSql, Token, runId);

        Assert.DoesNotContain(runId, reaped);
        Assert.Equal(JobRunStatuses.Running, status);
    }

    [Fact]
    public async Task ReapExpiredLeasesAsync_StoresTheAbandonedErrorCode_WhichTheCheckConstraintMustAdmit()
    {
        var runId = await SeedRunAsync(JobRunStatuses.Running, SeqOfAFreshRun, UpdatedBeyondTheAbandonedWindow);
        await SetLeaseAsync(runId, LapsedLease);
        var repository = new JobRunsRepository(_database.DataSource);

        await repository.ReapExpiredLeasesAsync(
            ExpiredLeaseReaper.AbandonedRunError,
            JobErrorCodes.Abandoned,
            AbandonedAfter.TotalSeconds,
            Token);

        var errorCode = await _database.ScalarAsync<string>(ErrorCodeSql, Token, runId);
        var error = await _database.ScalarAsync<string>(ErrorSql, Token, runId);

        Assert.Equal(JobErrorCodes.Abandoned, errorCode);
        Assert.Equal(ExpiredLeaseReaper.AbandonedRunError, error);
    }

    [Fact]
    public async Task GetAsync_ForASeededRun_ReadsBackTheKindStatusSeqAndSummary()
    {
        var seededSeq = Generated.NewJobSeq();
        var runId = await SeedRunAsync(JobRunStatuses.Running, seededSeq, UpdatedJustNow);
        var repository = new JobRunsRepository(_database.DataSource);
        var keptValue = Generated.NewFieldValue();
        await repository.TryMarkSucceededAsync(
            runId, new { nested = new { kept = keptValue } }, Token);

        var run = await repository.GetAsync(runId, Token);

        Assert.NotNull(run);
        Assert.Equal(JobRunKinds.LibraryRefresh, run.Kind);
        Assert.Equal(JobRunStatuses.Succeeded, run.Status);
        Assert.Equal(seededSeq, run.Seq);
        Assert.Equal(_identitySub, run.IdentitySub);
        Assert.Contains(keptValue, run.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ForAnUnknownRun_ReturnsNull()
    {
        var repository = new JobRunsRepository(_database.DataSource);
        var unknown = Guid.NewGuid();

        var run = await repository.GetAsync(unknown, Token);

        var rowCount = await _database.ScalarAsync<long>(RunExistsSql, Token, unknown);

        Assert.Null(run);
        Assert.Equal(NoRows, rowCount);
    }

    private async Task<Guid> SeedRunAsync(string status, int seq, TimeSpan updatedAtOffset)
    {
        var newRunId = Guid.NewGuid();
        return await _database.ScalarAsync<Guid>(
            SeedRunSql,
            Token,
            newRunId,
            JobRunKinds.LibraryRefresh,
            _identitySub,
            status,
            seq,
            updatedAtOffset.TotalSeconds);
    }

    private async Task SetLeaseAsync(Guid runId, TimeSpan leaseOffset) =>
        await _database.ScalarAsync<Guid>(SetLeaseSql, Token, runId, leaseOffset.TotalSeconds);
}
