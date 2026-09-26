namespace Functions.Tests.Unit;

using Functions.Curator;
using Functions.Curator.Jobs;
using Functions.Curator.Library;
using Functions.Tests.Unit.TestSupport;

[Trait("Category", "Unit")]
public sealed class AccountActionLogRepositoryTests
{
    private static readonly Func<Exception, string?> NoPlannedStops = static _ => null;

    [Fact]
    public async Task RecordAsync_WritesAStartedRowBeforeTheBodyRunsAndMarksItCompletedAfter()
    {
        // Arrange
        var auditDb = RecordingAuditDb();
        var repository = new AccountActionLogRepository(auditDb);
        var identitySub = Guid.NewGuid();
        var bodyResult = Guid.NewGuid();
        var rowsWhenTheBodyRan = -1;

        // Act
        var result = await repository.RecordAsync(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            bodyResult.ToString(),
            _ =>
            {
                rowsWhenTheBodyRan = auditDb.ExecutedCommands.Count;
                return Task.FromResult(bodyResult);
            },
            NoPlannedStops,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(bodyResult, result);
        Assert.Equal(1, rowsWhenTheBodyRan);
        var begin = auditDb.ExecutedCommands[0];
        Assert.Equal(AccountActionLogRepository.OutcomeStarted, begin.Parameters[CuratorSqlParameters.Outcome].Value);
        Assert.Equal(identitySub, begin.Parameters[CuratorSqlParameters.IdentitySub].Value);
        var finish = auditDb.ExecutedCommands[1];
        Assert.Equal(AccountActionLogRepository.OutcomeCompleted, finish.Parameters[CuratorSqlParameters.Outcome].Value);
    }

    [Fact]
    public async Task RecordAsync_NeverRunsTheBody_WhenTheStartedRowCannotBeWritten()
    {
        // Arrange
        var auditDb = new FakeDbDataSource();
        auditDb.Enqueue(FakeDbCommand.ThatThrowsOnExecute());
        var repository = new AccountActionLogRepository(auditDb);
        var identitySub = Guid.NewGuid();
        var bodyRan = false;

        // Act
        var exception = await Record.ExceptionAsync(() => repository.RecordAsync(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            null,
            _ =>
            {
                bodyRan = true;
                return Task.FromResult(true);
            },
            NoPlannedStops,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<FakeDbException>(exception);
        Assert.False(bodyRan);
    }

    [Fact]
    public async Task RecordAsync_MarksTheRowFailedAndRethrows_WhenTheBodyThrows()
    {
        // Arrange
        var auditDb = RecordingAuditDb();
        var repository = new AccountActionLogRepository(auditDb);
        var identitySub = Guid.NewGuid();
        var failureMessage = Guid.NewGuid().ToString();
        var bodyFailure = new InvalidOperationException(failureMessage);

        // Act
        var exception = await Record.ExceptionAsync(() => repository.RecordAsync<bool>(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            null,
            _ => throw bodyFailure,
            NoPlannedStops,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(bodyFailure, exception);
        var finish = auditDb.ExecutedCommands[1];
        Assert.Equal(AccountActionLogRepository.OutcomeFailed, finish.Parameters[CuratorSqlParameters.Outcome].Value);
    }

    [Fact]
    public async Task RecordAsync_RecordsAPausedRunAsCompletedWithThePauseInTheDetail()
    {
        // Arrange
        var auditDb = RecordingAuditDb();
        var repository = new AccountActionLogRepository(auditDb);
        var runId = Guid.NewGuid();
        var identitySub = Guid.NewGuid();
        var remainingCount = Random.Shared.Next(1, 100);
        var pause = ContinuationScheduledException.TimeBudgetExhausted(remainingCount);

        // Act
        var exception = await Record.ExceptionAsync(() => repository.RecordAsync<bool>(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            runId.ToString(),
            _ => throw pause,
            LibraryRefreshRunDetails.PlannedStop(runId),
            TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(pause, exception);
        var finish = auditDb.ExecutedCommands[1];
        Assert.Equal(AccountActionLogRepository.OutcomeCompleted, finish.Parameters[CuratorSqlParameters.Outcome].Value);
        Assert.Equal(LibraryRefreshRunDetails.Paused(runId), finish.Parameters[CuratorSqlParameters.Detail].Value);
    }

    [Fact]
    public async Task RecordAsync_RecordsAStoodDownRunAsCompletedWithTheStandDownInTheDetail()
    {
        // Arrange
        var auditDb = RecordingAuditDb();
        var repository = new AccountActionLogRepository(auditDb);
        var runId = Guid.NewGuid();
        var standDown = JobRunStoodDownException.ForRun(runId);
        var identitySub = Guid.NewGuid();

        // Act
        var exception = await Record.ExceptionAsync(() => repository.RecordAsync<bool>(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            runId.ToString(),
            _ => throw standDown,
            LibraryRefreshRunDetails.PlannedStop(runId),
            TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(standDown, exception);
        var finish = auditDb.ExecutedCommands[1];
        Assert.Equal(AccountActionLogRepository.OutcomeCompleted, finish.Parameters[CuratorSqlParameters.Outcome].Value);
        Assert.Equal(LibraryRefreshRunDetails.StoodDown(runId), finish.Parameters[CuratorSqlParameters.Detail].Value);
    }

    [Fact]
    public async Task RecordAsync_RecordsACancelledRunAsFailedAndKeepsTheStartedDetail()
    {
        // Arrange
        var auditDb = RecordingAuditDb();
        var repository = new AccountActionLogRepository(auditDb);
        var startedDetail = Guid.NewGuid().ToString();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelledToken = cancellation.Token;
        var identitySub = Guid.NewGuid();

        // Act
        var exception = await Record.ExceptionAsync(() => repository.RecordAsync(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            startedDetail,
            _ => Task.FromCanceled<bool>(cancelledToken),
            NoPlannedStops,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        var finish = auditDb.ExecutedCommands[1];
        Assert.Equal(AccountActionLogRepository.OutcomeFailed, finish.Parameters[CuratorSqlParameters.Outcome].Value);
        Assert.Equal(AccountActionLogRepository.Cancelled(startedDetail), finish.Parameters[CuratorSqlParameters.Detail].Value);
    }

    [Fact]
    public async Task RecordAsync_KeepsTheBodyException_WhenTheOutcomeCannotBeWritten()
    {
        // Arrange
        var logId = Guid.NewGuid();
        var auditDb = new FakeDbDataSource();
        auditDb.Enqueue(FakeDbCommand.WithScalarResult(logId));
        auditDb.Enqueue(FakeDbCommand.ThatThrowsOnExecute());
        var repository = new AccountActionLogRepository(auditDb);
        var identitySub = Guid.NewGuid();
        var failureMessage = Guid.NewGuid().ToString();
        var bodyFailure = new InvalidOperationException(failureMessage);

        // Act
        var exception = await Record.ExceptionAsync(() => repository.RecordAsync<bool>(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            null,
            _ => throw bodyFailure,
            NoPlannedStops,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(bodyFailure, exception);
    }

    [Fact]
    public async Task RecordAsync_Throws_WhenTheOutcomeOfASuccessfulBodyCannotBeWritten()
    {
        // Arrange
        var logId = Guid.NewGuid();
        var auditDb = new FakeDbDataSource();
        auditDb.Enqueue(FakeDbCommand.WithScalarResult(logId));
        auditDb.Enqueue(FakeDbCommand.ThatThrowsOnExecute());
        var repository = new AccountActionLogRepository(auditDb);
        var identitySub = Guid.NewGuid();

        // Act
        var exception = await Record.ExceptionAsync(() => repository.RecordAsync(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            null,
            _ => Task.FromResult(true),
            NoPlannedStops,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<FakeDbException>(exception);
    }

    [Fact]
    public async Task FinishAsync_Throws_WhenNoStartedRowCarriesTheId()
    {
        // Arrange
        var auditDb = new FakeDbDataSource();
        auditDb.Enqueue(FakeDbCommand.WithNonQueryResult(0));
        var repository = new AccountActionLogRepository(auditDb);
        var unstartedLogId = Guid.NewGuid();

        // Act
        var exception = await Record.ExceptionAsync(() => repository.FinishAsync(
            unstartedLogId,
            AccountActionLogRepository.OutcomeCompleted,
            null,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    private static FakeDbDataSource RecordingAuditDb()
    {
        var logId = Guid.NewGuid();
        var auditDb = new FakeDbDataSource();
        auditDb.Enqueue(FakeDbCommand.WithScalarResult(logId));
        auditDb.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        return auditDb;
    }
}
