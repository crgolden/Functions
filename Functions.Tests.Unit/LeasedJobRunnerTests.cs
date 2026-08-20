namespace Functions.Tests.Unit;

using System.Data;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Curator.Enrichment;
using Curator.Jobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class LeasedJobRunnerTests
{
    private const string LeaseRenewal = "SET lease_expires_at = now()";

    private static readonly string RunId = Guid.NewGuid().ToString();

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RunAsync_WhenBodyIsNotJson_DeadLettersWithoutClaimingTheRun()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var runner = NewRunner(dataSource);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("not json"));
        var actions = DeadLetteringActions(message, LeasedJobRunner.MalformedPayload);

        // Act
        await runner.RunAsync<EnrichmentRunMessage>(message, actions.Object, NeverRuns, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(dataSource.ExecutedCommands);
        actions.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_WhenARequiredMemberIsMissing_DeadLettersWithoutClaimingTheRun()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var runner = NewRunner(dataSource);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("{}"));
        var actions = DeadLetteringActions(message, LeasedJobRunner.MalformedPayload);

        // Act
        await runner.RunAsync<EnrichmentRunMessage>(message, actions.Object, NeverRuns, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(dataSource.ExecutedCommands);
        actions.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_ClaimsTheRunWithACompareAndSwapOnSeqAndLease()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(RunId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var runner = NewRunner(dataSource);
        var message = MessageFor(RunId, NewSeq());
        var actions = CompletingActions(message);

        // Act
        await runner.RunAsync<EnrichmentRunMessage>(message, actions.Object, Succeeds, TestContext.Current.CancellationToken);

        // Assert
        var claim = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("UPDATE job_runs", claim, StringComparison.Ordinal);
        Assert.Contains("seq = @expected_seq", claim, StringComparison.Ordinal);
        Assert.Contains("status NOT IN ('succeeded', 'failed')", claim, StringComparison.Ordinal);
        Assert.Contains("lease_expires_at <= now()", claim, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenSeqIsStale_SettlesWithoutReprocessing()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        dataSource.Enqueue(FakeDbCommand.WithReader(JobRunTable(JobRunStatuses.RateLimited, error: null)));
        var runner = NewRunner(dataSource);
        var message = MessageFor(RunId, NewSeq());
        var actions = CompletingActions(message);
        var ran = false;

        // Act
        await runner.RunAsync<EnrichmentRunMessage>(
            message,
            actions.Object,
            (_, _) =>
            {
                ran = true;
                return Task.FromResult<object?>(null);
            },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.False(ran);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenStaleRedeliveryOfAFailedRun_DeadLettersSoItSurfacesInTheDlq()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        var priorFailureError = $"failure-{Guid.NewGuid():N}";
        dataSource.Enqueue(FakeDbCommand.WithReader(JobRunTable(JobRunStatuses.Failed, priorFailureError)));
        var runner = NewRunner(dataSource);
        var message = MessageFor(RunId, NewSeq());
        var actions = DeadLetteringActions(message, LeasedJobRunner.ProcessingFailed, priorFailureError);

        // Act
        await runner.RunAsync<EnrichmentRunMessage>(message, actions.Object, NeverRuns, TestContext.Current.CancellationToken);

        // Assert
        actions.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_WhenWorkSignalsRateLimitRetry_CompletesWithoutMarkingFailed()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(RunId));
        var runner = NewRunner(dataSource);
        var message = MessageFor(RunId, NewSeq());
        var actions = CompletingActions(message);
        var retryAfterSeconds = Random.Shared.Next(60, 7200);

        // Act
        await runner.RunAsync<EnrichmentRunMessage>(
            message,
            actions.Object,
            (_, _) => throw ContinuationScheduledException.RateLimited(
                EnrichmentProviderNames.OpenCritic, retryAfterSeconds),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(dataSource.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        actions.Verify(
            a => a.DeadLetterMessageAsync(
                It.IsAny<ServiceBusReceivedMessage>(),
                It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenWorkThrows_MarksFailedThenDeadLetters()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(RunId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var runner = NewRunner(dataSource);
        var message = MessageFor(RunId, NewSeq());
        var actions = DeadLetteringActions(message, JobErrorCodes.Unexpected, LeasedJobRunner.GenericMessage);
        var thrownFailure = $"unexpected-{Guid.NewGuid():N}";

        // Act
        await runner.RunAsync<EnrichmentRunMessage>(
            message,
            actions.Object,
            (_, _) => throw new InvalidOperationException(thrownFailure),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("status = 'failed'", dataSource.ExecutedCommands[1].CapturedCommandText, StringComparison.Ordinal);
        Assert.Equal(JobErrorCodes.Unexpected, dataSource.ExecutedCommands[1].Parameters["@error_code"].Value);
        actions.VerifyAll();
    }

    [Fact]
    public async Task RunAsync_WhenLockIsLostWhileSettling_SwallowsItBecauseTheStatusWriteAlreadyCommitted()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(RunId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var runner = NewRunner(dataSource);
        var message = MessageFor(RunId, NewSeq());
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost));

        // Act
        await runner.RunAsync<EnrichmentRunMessage>(message, actions.Object, Succeeds, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("status = 'succeeded'", dataSource.ExecutedCommands[1].CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RenewsTheLeaseWhileTheWorkRuns()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(RunId));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(RunId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var timeProvider = new FakeTimeProvider();
        var runner = new LeasedJobRunner(
            new JobRunsRepository(dataSource), HeartbeatInterval, timeProvider);
        var message = MessageFor(RunId, NewSeq());
        var actions = CompletingActions(message);
        var renewed = dataSource.WhenExecuted(LeaseRenewal);

        // Act
        var run = runner.RunAsync<EnrichmentRunMessage>(
            message, actions.Object, Work(renewed), TestContext.Current.CancellationToken);
        timeProvider.Advance(HeartbeatInterval);
        await renewed;
        await run;

        // Assert
        Assert.Contains(
            dataSource.ExecutedCommands,
            cmd => cmd.ExecutedSql.Contains(LeaseRenewal, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_GivesTheHeartbeatItsOwnConnectionRatherThanSharingOneWithTheTerminalWrite()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(RunId));
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(RunId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var timeProvider = new FakeTimeProvider();
        var runner = new LeasedJobRunner(
            new JobRunsRepository(dataSource), HeartbeatInterval, timeProvider);
        var message = MessageFor(RunId, NewSeq());
        var actions = CompletingActions(message);
        var renewed = dataSource.WhenExecuted(LeaseRenewal);

        // Act
        var run = runner.RunAsync<EnrichmentRunMessage>(
            message, actions.Object, Work(renewed), TestContext.Current.CancellationToken);
        timeProvider.Advance(HeartbeatInterval);
        await renewed;
        await run;

        // Assert
        Assert.Equal(dataSource.ExecutedCommands.Count, dataSource.ConnectionsCreated);
        Assert.All(dataSource.Connections, connection => Assert.Equal(ConnectionState.Closed, connection.State));
    }

    private static Func<EnrichmentRunMessage, CancellationToken, Task<object?>> Work(Task heldUntil) =>
        async (_, _) =>
        {
            await heldUntil;
            return null;
        };

    private static LeasedJobRunner NewRunner(FakeDbDataSource dataSource) =>
        new(new JobRunsRepository(dataSource), TimeSpan.FromMinutes(5));

    private static Task<object?> NeverRuns(EnrichmentRunMessage payload, CancellationToken token) =>
        throw new InvalidOperationException("handler must not run");

    private static Task<object?> Succeeds(EnrichmentRunMessage payload, CancellationToken token) =>
        Task.FromResult<object?>(null);

    private static int NewSeq() => Random.Shared.Next(0, 1000);

    private static ServiceBusReceivedMessage MessageFor(string runId, int seq) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(JsonSerializer.Serialize(new { run_id = runId, seq })));

    private static Mock<ServiceBusMessageActions> CompletingActions(ServiceBusReceivedMessage message)
    {
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return actions;
    }

    private static Mock<ServiceBusMessageActions> DeadLetteringActions(
        ServiceBusReceivedMessage message, string reason)
    {
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                reason,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return actions;
    }

    private static Mock<ServiceBusMessageActions> DeadLetteringActions(
        ServiceBusReceivedMessage message, string reason, string description)
    {
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                reason,
                description,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return actions;
    }

    private static DataTable JobRunTable(string status, string? error)
    {
        var table = new DataTable();
        table.Columns.Add("run_id", typeof(Guid));
        table.Columns.Add("kind", typeof(string));
        table.Columns.Add("identity_sub", typeof(Guid));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("error", typeof(string));
        table.Columns.Add("seq", typeof(int));
        table.Columns.Add("result_summary", typeof(string));
        table.Rows.Add(Guid.Parse(RunId), JobRunKinds.Enrichment, DBNull.Value, status, (object?)error ?? DBNull.Value, 0, DBNull.Value);
        return table;
    }
}
