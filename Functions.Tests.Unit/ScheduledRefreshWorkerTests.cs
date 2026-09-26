namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using Azure.Messaging.ServiceBus;
using Functions.Curator;
using Functions.Curator.Jobs;
using Functions.Curator.Library;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Moq;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class ScheduledRefreshWorkerTests
{
    public static TheoryData<string, TimeSpan> CadenceIntervals() => new()
    {
        { RefreshCadences.Daily, ScheduledRefreshWorker.DailyInterval },
        { RefreshCadences.Weekly, ScheduledRefreshWorker.WeeklyInterval },
        { RefreshCadences.Monthly, ScheduledRefreshWorker.MonthlyInterval },
    };

    [Fact]
    public async Task Run_WhenPayloadIsNull_DeadLettersWithoutDbAccess()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (factory, _, _) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson<ScheduledRefreshMessage?>(null));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, LeasedJobRunner.MalformedPayload, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, LeasedJobRunner.MalformedPayload, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenIdentitySubMissing_DeadLettersWithoutDbAccess()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (factory, _, _) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(JsonResponse.EmptyObject));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, LeasedJobRunner.MalformedPayload, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, LeasedJobRunner.MalformedPayload, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenNoScheduleExists_DiscardsWithoutDispatch()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, DateTimeOffset.UtcNow));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(connection.ExecutedCommands);
        Assert.Empty(sent);
        Assert.Empty(scheduled);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenSchedulePaused_DiscardsWithoutDispatch()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(
            DateTimeOffset.UtcNow,
            NewConsecutiveFailureCount(),
            ScheduledRefreshWorker.TooManyFailuresPausedReason,
            RefreshCadences.Weekly)));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var scheduledFor = DateTimeOffset.UtcNow;
        var message = Message(new ScheduledRefreshMessage(identitySub, scheduledFor));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(connection.ExecutedCommands);
        Assert.Empty(sent);
        Assert.Empty(scheduled);
    }

    [Fact]
    public async Task Run_WhenScheduledForDoesNotMatchStoredNextRunAt_DiscardsAsStale()
    {
        // Arrange
        var storedNextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(storedNextRunAt, 0, null, RefreshCadences.Weekly)));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var driftBeyondTheTolerance = NewScheduleDriftBeyondTolerance();
        var message = Message(new ScheduledRefreshMessage(identitySub, storedNextRunAt - driftBeyondTheTolerance));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(connection.ExecutedCommands);
        Assert.Empty(sent);
        Assert.Empty(scheduled);
    }

    [Fact]
    public async Task Run_WhenScheduleCurrentAndNoActiveRun_DispatchesAndAdvancesSchedule()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, RefreshCadences.Weekly)));
        connection.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var auditDb = RecordingAuditDb();
        var worker = CreateWorker(connection, factory, auditDb);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            auditDb.ExecutedCommands,
            begin => Assert.Equal(AccountActionLogRepository.OutcomeStarted, begin.Parameters[CuratorSqlParameters.Outcome].Value),
            finish => Assert.Equal(AccountActionLogRepository.OutcomeCompleted, finish.Parameters[CuratorSqlParameters.Outcome].Value));
        var audit = auditDb.ExecutedCommands[0];
        Assert.Contains("INSERT INTO account_action_log", audit.ExecutedSql, StringComparison.Ordinal);
        Assert.Equal(identitySub, audit.Parameters[CuratorSqlParameters.IdentitySub].Value);
        Assert.Equal(AccountActionLogRepository.LibraryRefreshRequested, audit.Parameters[CuratorSqlParameters.Action].Value);
        var outcome = auditDb.ExecutedCommands[^1];
        Assert.Contains("UPDATE account_action_log", outcome.ExecutedSql, StringComparison.Ordinal);
        Assert.Collection(
            connection.ExecutedCommands,
            loadSchedule => Assert.Contains("FROM user_refresh_schedules", loadSchedule.CommandText, StringComparison.Ordinal),
            loadLatestRun => Assert.Contains("FROM job_runs", loadLatestRun.CommandText, StringComparison.Ordinal),
            insertRun => Assert.Contains("INSERT INTO job_runs", insertRun.CommandText, StringComparison.Ordinal),
            advanceSchedule => Assert.Contains("UPDATE user_refresh_schedules", advanceSchedule.CommandText, StringComparison.Ordinal));
        var insertRunCommand = connection.ExecutedCommands.Single(command => command.CommandText.Contains("INSERT INTO job_runs", StringComparison.Ordinal));
        Assert.Equal(insertRunCommand.Parameters[CuratorSqlParameters.RunId].Value?.ToString(), audit.Parameters[CuratorSqlParameters.Detail].Value);
        var advanceScheduleCommandText = connection.ExecutedCommands.Single(command => command.CommandText.Contains("UPDATE user_refresh_schedules", StringComparison.Ordinal)).CommandText;
        Assert.Contains("paused_reason = NULL", advanceScheduleCommandText, StringComparison.Ordinal);
        var dispatched = Assert.Single(sent);
        var libraryRefresh = Assert.IsType<LibraryRefreshMessage>(
            dispatched.Body.ToObjectFromJson<LibraryRefreshMessage>());
        Assert.Equal(identitySub, libraryRefresh.IdentitySub);
        var nextTick = Assert.Single(scheduled);
        var nextTickPayload = Assert.IsType<ScheduledRefreshMessage>(
            nextTick.Message.Body.ToObjectFromJson<ScheduledRefreshMessage>());
        Assert.Equal(identitySub, nextTickPayload.IdentitySub);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenTheHistoryRowCannotBeWritten_DispatchesNothingAndLeavesTheMessageUnsettled()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, RefreshCadences.Weekly)));
        connection.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var (factory, sent, scheduled) = CreateServiceBus();
        var auditDb = new FakeDbDataSource();
        auditDb.Enqueue(FakeDbCommand.ThatThrowsOnExecute());
        var worker = CreateWorker(connection, factory, auditDb);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);

        // Act
        var exception = await Record.ExceptionAsync(
            () => worker.Run(message, actions.Object, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(exception);
        Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO job_runs", StringComparison.Ordinal));
        Assert.Empty(sent);
        Assert.Empty(scheduled);
    }

    [Fact]
    public async Task Run_WhenTheDispatchFails_MarksTheHistoryRowFailed()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, RefreshCadences.Weekly)));
        connection.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        connection.Enqueue(FakeDbCommand.ThatThrowsOnExecute());
        var (factory, sent, _) = CreateServiceBus();
        var auditDb = RecordingAuditDb();
        var worker = CreateWorker(connection, factory, auditDb);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);

        // Act
        var exception = await Record.ExceptionAsync(
            () => worker.Run(message, actions.Object, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(exception);
        Assert.Empty(sent);
        var outcome = auditDb.ExecutedCommands[^1];
        Assert.Contains("UPDATE account_action_log", outcome.ExecutedSql, StringComparison.Ordinal);
        Assert.Equal(AccountActionLogRepository.OutcomeFailed, outcome.Parameters[CuratorSqlParameters.Outcome].Value);
    }

    [Fact]
    public async Task Run_WhenPreviousRunStillActive_SkipsDispatchButStillAdvancesSchedule()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, RefreshCadences.Weekly)));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(NewRunId(), JobRunStatuses.Running, null)));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var auditDb = new FakeDbDataSource();
        var worker = CreateWorker(connection, factory, auditDb);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            connection.ExecutedCommands,
            loadSchedule => Assert.Contains("FROM user_refresh_schedules", loadSchedule.CommandText, StringComparison.Ordinal),
            loadLatestRun => Assert.Contains("FROM job_runs", loadLatestRun.CommandText, StringComparison.Ordinal),
            advanceSchedule => Assert.Contains("UPDATE user_refresh_schedules", advanceSchedule.CommandText, StringComparison.Ordinal));
        Assert.DoesNotContain(connection.ExecutedCommands, c => c.CommandText.Contains("INSERT INTO job_runs", StringComparison.Ordinal));
        Assert.Empty(auditDb.ExecutedCommands);
        Assert.Empty(sent);
        Assert.Single(scheduled);
    }

    [Fact]
    public async Task Run_WhenPreviousRunFailedWithGenericError_IncrementsFailuresAndDispatchesAgain()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 1, null, RefreshCadences.Weekly)));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(NewRunId(), JobRunStatuses.Failed, JobErrorCodes.Unexpected)));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(sent);
        Assert.Single(scheduled);
        var advanceSchedule = connection.ExecutedCommands.Single(command => command.CommandText.Contains("UPDATE user_refresh_schedules", StringComparison.Ordinal));
        Assert.Contains("paused_reason = NULL", advanceSchedule.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenPreviousRunFailedWithExpiredPsnLink_PausesWithoutDispatchOrNextTick()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, RefreshCadences.Weekly)));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(NewRunId(), JobRunStatuses.Failed, JobErrorCodes.PsnLinkExpired)));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(sent);
        Assert.Empty(scheduled);
        Assert.Collection(
            connection.ExecutedCommands,
            loadSchedule => Assert.Contains("FROM user_refresh_schedules", loadSchedule.CommandText, StringComparison.Ordinal),
            loadLatestRun => Assert.Contains("FROM job_runs", loadLatestRun.CommandText, StringComparison.Ordinal),
            pauseUpdate => Assert.Contains("paused_reason = @paused_reason", pauseUpdate.CommandText, StringComparison.Ordinal));
        var pauseUpdateCommand = connection.ExecutedCommands.Single(command => command.CommandText.Contains("paused_reason = @paused_reason", StringComparison.Ordinal));
        Assert.Equal(ScheduledRefreshWorker.PsnLinkExpiredPausedReason, pauseUpdateCommand.Parameters[CuratorSqlParameters.PausedReason].Value);
    }

    [Fact]
    public async Task Run_WhenConsecutiveFailuresReachThreshold_Pauses()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var maxConsecutiveFailures = NewConsecutiveFailureCount();
        var failuresBeforeThisRun = maxConsecutiveFailures - 1;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, failuresBeforeThisRun, null, RefreshCadences.Weekly)));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(NewRunId(), JobRunStatuses.Failed, JobErrorCodes.Unexpected)));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(
                CuratorConfigurationKeys.ScheduledRefreshMaxConsecutiveFailures,
                maxConsecutiveFailures.ToString(CultureInfo.InvariantCulture))])
            .Build();
        var worker = CreateWorker(connection, factory, configuration: configuration);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(sent);
        Assert.Empty(scheduled);
        var pauseUpdate = connection.ExecutedCommands.Single(command => command.CommandText.Contains("paused_reason = @paused_reason", StringComparison.Ordinal));
        Assert.Equal(
            ScheduledRefreshWorker.TooManyFailuresPausedReason,
            pauseUpdate.Parameters[CuratorSqlParameters.PausedReason].Value);
        Assert.Equal(maxConsecutiveFailures, pauseUpdate.Parameters[CuratorSqlParameters.ConsecutiveFailures].Value);
    }

    [Fact]
    public async Task Run_WhenPreviousRunSucceeded_ResetsFailuresAndDispatchesAgain()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, NewConsecutiveFailureCount(), null, RefreshCadences.Monthly)));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(NewRunId(), JobRunStatuses.Succeeded, null)));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, _) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(sent);
        var advanceUpdate = connection.ExecutedCommands.Single(command => command.CommandText.Contains("UPDATE user_refresh_schedules", StringComparison.Ordinal));
        Assert.Equal(0, advanceUpdate.Parameters[CuratorSqlParameters.ConsecutiveFailures].Value);
    }

    [Fact]
    public async Task Run_WhenPreviousRunWasCancelled_TreatsItAsTerminalAndKeepsTheChainMoving()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var priorFailures = NewConsecutiveFailureCount();
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, priorFailures, null, RefreshCadences.Monthly)));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(NewRunId(), JobRunStatuses.Cancelled, null)));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, _) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var advanceUpdate = connection.ExecutedCommands.Single(command => command.CommandText.Contains("UPDATE user_refresh_schedules", StringComparison.Ordinal));

        Assert.Single(sent);
        Assert.Equal(0, advanceUpdate.Parameters[CuratorSqlParameters.ConsecutiveFailures].Value);
    }

    [Theory]
    [MemberData(nameof(CadenceIntervals))]
    public async Task Run_AdvancesTheScheduleByTheStoredCadencesOwnInterval(string cadence, TimeSpan expectedInterval)
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, cadence)));
        connection.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, _, scheduled) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);
        var beforeRun = DateTimeOffset.UtcNow;

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var afterRun = DateTimeOffset.UtcNow;
        var advanceUpdate = connection.ExecutedCommands.Single(command => command.CommandText.Contains("UPDATE user_refresh_schedules", StringComparison.Ordinal));
        var advancedTo = Assert.IsType<DateTimeOffset>(advanceUpdate.Parameters[CuratorSqlParameters.NextRunAt].Value);
        Assert.InRange(advancedTo, beforeRun + expectedInterval, afterRun + expectedInterval);
        var nextTick = Assert.Single(scheduled);
        Assert.Equal(advancedTo, nextTick.ScheduledFor);
    }

    [Fact]
    public async Task Run_WhenTheStoredCadenceHasNoDefinedInterval_ThrowsInsteadOfFallingBackToAnotherCadence()
    {
        // Arrange
        var nextRunAt = DateTimeOffset.UtcNow;
        var cadenceNoIntervalIsDefinedFor = Guid.NewGuid().ToString();
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, cadenceNoIntervalIsDefinedFor)));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = CreateWorker(connection, factory);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);

        // Act
        var exception = await Record.ExceptionAsync(
            () => worker.Run(message, actions.Object, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Single(connection.ExecutedCommands);
        Assert.Empty(sent);
        Assert.Empty(scheduled);
    }

    private static ScheduledRefreshWorker CreateWorker(
        FakeDbConnection connection,
        IAzureClientFactory<ServiceBusClient> factory,
        FakeDbDataSource? auditDb = null,
        IConfiguration? configuration = null) =>
        new(
            connection,
            factory,
            new AccountActionLogRepository(auditDb ?? RecordingAuditDb()),
            configuration ?? ThresholdAboveEveryGeneratedFailureCount());

    private static FakeDbDataSource RecordingAuditDb()
    {
        var logId = Guid.NewGuid();
        var auditDb = new FakeDbDataSource();
        auditDb.Enqueue(FakeDbCommand.WithScalarResult(logId));
        auditDb.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        return auditDb;
    }

    private static IConfiguration ThresholdAboveEveryGeneratedFailureCount()
    {
        var threshold = Random.Shared.Next(21, 41);
        return new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(
                CuratorConfigurationKeys.ScheduledRefreshMaxConsecutiveFailures,
                threshold.ToString(CultureInfo.InvariantCulture))])
            .Build();
    }

    private static ServiceBusReceivedMessage Message(ScheduledRefreshMessage payload) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));

    private static Mock<ServiceBusMessageActions> CompletingActions(ServiceBusReceivedMessage message)
    {
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return actions;
    }

    private static DataTable ScheduleTable(
        DateTimeOffset nextRunAt,
        int consecutiveFailures,
        string? pausedReason,
        string cadence)
    {
        var table = FakeResultSet.WithColumns(
            typeof(DateTimeOffset),
            typeof(int),
            typeof(string),
            typeof(string));
        table.Rows.Add(nextRunAt, consecutiveFailures, (object?)pausedReason ?? DBNull.Value, cadence);
        return table;
    }

    private static DataTable LatestRunTable(Guid runId, string status, string? errorCode)
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string));
        table.Rows.Add(runId, status, (object?)errorCode ?? DBNull.Value);
        return table;
    }

    private static (IAzureClientFactory<ServiceBusClient> Factory, List<ServiceBusMessage> Sent, List<(ServiceBusMessage Message, DateTimeOffset ScheduledFor)> Scheduled) CreateServiceBus()
    {
        var sent = new List<ServiceBusMessage>();
        var scheduled = new List<(ServiceBusMessage Message, DateTimeOffset ScheduledFor)>();
        var sender = new Mock<ServiceBusSender>();
        sender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns((ServiceBusMessage m, CancellationToken _) =>
            {
                sent.Add(m);
                return Task.CompletedTask;
            });
        sender
            .Setup(s => s.ScheduleMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((ServiceBusMessage m, DateTimeOffset t, CancellationToken _) =>
            {
                scheduled.Add((m, t));
                return Task.FromResult(1L);
            });
        sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var client = new Mock<ServiceBusClient>();
        client.Setup(c => c.CreateSender(It.IsAny<string>())).Returns(sender.Object);

        var factory = new Mock<IAzureClientFactory<ServiceBusClient>>();
        factory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(client.Object);

        return (factory.Object, sent, scheduled);
    }
}
