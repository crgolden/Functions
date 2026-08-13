namespace Functions.Tests.Unit;

using System.Data;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ScheduledRefreshWorkerTests
{
    [Fact]
    public async Task Run_WhenPayloadIsNull_DeadLettersWithoutDbAccess()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (factory, _, _) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenIdentitySubMissing_DeadLettersWithoutDbAccess()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (factory, _, _) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("{}"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenNoScheduleExists_DiscardsWithoutDispatch()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
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
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(DateTime.UtcNow, 3, "too-many-consecutive-failures", "weekly")));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var identitySub = Guid.NewGuid();
        var scheduledFor = DateTime.UtcNow;
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
        var storedNextRunAt = DateTime.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(storedNextRunAt, 0, null, "weekly")));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, storedNextRunAt.AddDays(-7)));
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
        var nextRunAt = DateTime.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, "weekly")));
        connection.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(4, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO job_runs", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
        Assert.Contains("UPDATE user_refresh_schedules", connection.ExecutedCommands[3].CommandText, StringComparison.Ordinal);
        Assert.Contains("paused_reason = NULL", connection.ExecutedCommands[3].CommandText, StringComparison.Ordinal);
        var dispatched = Assert.Single(sent);
        var libraryRefresh = dispatched.Body.ToObjectFromJson<LibraryRefreshMessage>();
        Assert.Equal(identitySub, libraryRefresh!.IdentitySub);
        var nextTick = Assert.Single(scheduled);
        var nextTickPayload = nextTick.Message.Body.ToObjectFromJson<ScheduledRefreshMessage>();
        Assert.Equal(identitySub, nextTickPayload!.IdentitySub);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenPreviousRunStillActive_SkipsDispatchButStillAdvancesSchedule()
    {
        // Arrange
        var nextRunAt = DateTime.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, "weekly")));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(Guid.NewGuid(), "running", null)));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.DoesNotContain(connection.ExecutedCommands, c => c.CommandText.Contains("INSERT INTO job_runs", StringComparison.Ordinal));
        Assert.Empty(sent);
        Assert.Single(scheduled);
    }

    [Fact]
    public async Task Run_WhenPreviousRunFailedWithGenericError_IncrementsFailuresAndDispatchesAgain()
    {
        // Arrange
        var nextRunAt = DateTime.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 1, null, "weekly")));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(Guid.NewGuid(), "failed", "The job failed unexpectedly. If this keeps happening, contact support.")));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(sent);
        Assert.Single(scheduled);
        Assert.Contains("UPDATE user_refresh_schedules", connection.ExecutedCommands[3].CommandText, StringComparison.Ordinal);
        Assert.Contains("paused_reason = NULL", connection.ExecutedCommands[3].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenPreviousRunFailedWithExpiredPsnLink_PausesWithoutDispatchOrNextTick()
    {
        // Arrange
        var nextRunAt = DateTime.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, "weekly")));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(Guid.NewGuid(), "failed", "Your PlayStation Network link has expired or was rejected. Re-link your account and try again.")));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(sent);
        Assert.Empty(scheduled);
        Assert.Equal(3, connection.ExecutedCommands.Count);
        var pauseUpdate = connection.ExecutedCommands[2];
        Assert.Contains("paused_reason = @paused_reason", pauseUpdate.CommandText, StringComparison.Ordinal);
        Assert.Equal("psn-link-expired", pauseUpdate.Parameters["@paused_reason"].Value);
    }

    [Fact]
    public async Task Run_WhenConsecutiveFailuresReachThreshold_Pauses()
    {
        // Arrange
        var nextRunAt = DateTime.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 0, null, "weekly")));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(Guid.NewGuid(), "failed", "The job failed unexpectedly. If this keeps happening, contact support.")));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, scheduled) = CreateServiceBus();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("ScheduledRefreshMaxConsecutiveFailures", "1")])
            .Build();
        var worker = new ScheduledRefreshWorker(connection, factory, configuration);
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(sent);
        Assert.Empty(scheduled);
        var pauseUpdate = connection.ExecutedCommands[2];
        Assert.Equal("too-many-consecutive-failures", pauseUpdate.Parameters["@paused_reason"].Value);
        Assert.Equal(1, pauseUpdate.Parameters["@consecutive_failures"].Value);
    }

    [Fact]
    public async Task Run_WhenPreviousRunSucceeded_ResetsFailuresAndDispatchesAgain()
    {
        // Arrange
        var nextRunAt = DateTime.UtcNow;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(ScheduleTable(nextRunAt, 2, null, "monthly")));
        connection.Enqueue(FakeDbCommand.WithReader(LatestRunTable(Guid.NewGuid(), "succeeded", null)));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        connection.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var (factory, sent, _) = CreateServiceBus();
        var worker = new ScheduledRefreshWorker(connection, factory, EmptyConfiguration());
        var identitySub = Guid.NewGuid();
        var message = Message(new ScheduledRefreshMessage(identitySub, nextRunAt));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(sent);
        var advanceUpdate = connection.ExecutedCommands[3];
        Assert.Equal(0, advanceUpdate.Parameters["@consecutive_failures"].Value);
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static ServiceBusReceivedMessage Message(ScheduledRefreshMessage payload) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));

    private static Mock<ServiceBusMessageActions> CompletingActions(ServiceBusReceivedMessage message)
    {
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return actions;
    }

    private static DataTable ScheduleTable(DateTime nextRunAt, int consecutiveFailures, string? pausedReason, string cadence)
    {
        var table = new DataTable();
        table.Columns.Add("next_run_at", typeof(DateTime));
        table.Columns.Add("consecutive_failures", typeof(int));
        table.Columns.Add("paused_reason", typeof(string));
        table.Columns.Add("cadence", typeof(string));
        table.Rows.Add(nextRunAt, consecutiveFailures, (object?)pausedReason ?? DBNull.Value, cadence);
        return table;
    }

    private static DataTable LatestRunTable(Guid runId, string status, string? error)
    {
        var table = new DataTable();
        table.Columns.Add("run_id", typeof(Guid));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("error", typeof(string));
        table.Rows.Add(runId, status, (object?)error ?? DBNull.Value);
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
        factory.Setup(f => f.CreateClient("crgolden")).Returns(client.Object);

        return (factory.Object, sent, scheduled);
    }
}
