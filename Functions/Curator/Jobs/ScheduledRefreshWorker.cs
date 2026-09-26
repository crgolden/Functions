namespace Functions.Curator.Jobs;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Functions.Curator.Library;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class ScheduledRefreshWorker
{
    internal const string PsnLinkExpiredPausedReason = "psn-link-expired";
    internal const string TooManyFailuresPausedReason = "too-many-consecutive-failures";

    internal static readonly TimeSpan DailyInterval = TimeSpan.FromDays(1);
    internal static readonly TimeSpan WeeklyInterval = TimeSpan.FromDays(7);
    internal static readonly TimeSpan MonthlyInterval = TimeSpan.FromDays(30);

    private const string ScheduledRefreshQueue = "curator-scheduled-refresh";
    private const string UnknownCadenceError = "No refresh interval is defined for this cadence.";
    private const string IdentitySubParameter = CuratorSqlParameters.IdentitySub;

    private static readonly TimeSpan ScheduledForTolerance = TimeSpan.FromSeconds(1);
    private static readonly string[] TerminalStatuses =
        [JobRunStatuses.Succeeded, JobRunStatuses.Failed, JobRunStatuses.Cancelled];

    private readonly DbConnection _dbConnection;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly AccountActionLogRepository _auditRepository;
    private readonly int _maxConsecutiveFailures;

    public ScheduledRefreshWorker(
        [FromKeyedServices(CuratorServiceCollectionExtensions.CuratorServiceKey)] DbConnection dbConnection,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory,
        AccountActionLogRepository auditRepository,
        IConfiguration configuration)
    {
        _dbConnection = dbConnection;
        _serviceBusClient = serviceBusClientFactory.CreateClient(AzureClientNames.Crgolden);
        _auditRepository = auditRepository;
        _maxConsecutiveFailures =
            configuration.GetRequired<int>(CuratorConfigurationKeys.ScheduledRefreshMaxConsecutiveFailures);
    }

    [Function(nameof(ScheduledRefreshWorker))]
    public async Task Run(
        [ServiceBusTrigger(ScheduledRefreshQueue, Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var payload = ParsePayload(message);
        if (payload is null)
        {
            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason: LeasedJobRunner.MalformedPayload,
                cancellationToken: cancellationToken);
            return;
        }

        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(cancellationToken);
        }

        var schedule = await LoadScheduleAsync(payload.IdentitySub, cancellationToken);
        if (schedule is null
            || schedule.Value.PausedReason is not null
            || (schedule.Value.NextRunAt - payload.ScheduledFor).Duration() > ScheduledForTolerance)
        {
            Telemetry.Tracing.RecordHandledFailure("scheduled-refresh.discarded", payload.IdentitySub.ToString());
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        var interval = IntervalFor(schedule.Value.Cadence);
        var latestRun = await LoadLatestRunAsync(payload.IdentitySub, cancellationToken);
        var consecutiveFailures = schedule.Value.ConsecutiveFailures;
        string? pausedReason = null;

        if (latestRun is null || TerminalStatuses.Contains(latestRun.Value.Status))
        {
            (consecutiveFailures, pausedReason) = NextFailureState(latestRun, consecutiveFailures);
            if (pausedReason is null)
            {
                await DispatchLibraryRefreshAsync(payload.IdentitySub, cancellationToken);
            }
        }
        else
        {
            Telemetry.Tracing.RecordHandledFailure("scheduled-refresh.skipped-active-run", $"{payload.IdentitySub}: run {latestRun.Value.RunId} still {latestRun.Value.Status}");
        }

        var now = DateTimeOffset.UtcNow;
        if (pausedReason is null)
        {
            var nextRunAt = now + interval;
            await AdvanceScheduleAsync(payload.IdentitySub, now, nextRunAt, consecutiveFailures, cancellationToken);
            await PublishNextTickAsync(payload.IdentitySub, nextRunAt, cancellationToken);
        }
        else
        {
            await PauseScheduleAsync(payload.IdentitySub, now, consecutiveFailures, pausedReason, cancellationToken);
        }

        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }

    private static ScheduledRefreshMessage? ParsePayload(ServiceBusReceivedMessage message)
    {
        try
        {
            var payload = message.Body.ToObjectFromJson<ScheduledRefreshMessage>();
            return payload is null || payload.IdentitySub == Guid.Empty ? null : payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan IntervalFor(string cadence) => cadence switch
    {
        RefreshCadences.Daily => DailyInterval,
        RefreshCadences.Weekly => WeeklyInterval,
        RefreshCadences.Monthly => MonthlyInterval,
        _ => throw new ArgumentOutOfRangeException(nameof(cadence), cadence, UnknownCadenceError),
    };

    private (int ConsecutiveFailures, string? PausedReason) NextFailureState(
        (Guid RunId, string Status, string? ErrorCode)? latestRun,
        int consecutiveFailures)
    {
        if (latestRun is not { Status: JobRunStatuses.Failed } failedRun)
        {
            return (0, null);
        }

        var failures = consecutiveFailures + 1;
        if (string.Equals(failedRun.ErrorCode, JobErrorCodes.PsnLinkExpired, StringComparison.Ordinal))
        {
            return (failures, PsnLinkExpiredPausedReason);
        }

        return (failures, failures >= _maxConsecutiveFailures ? TooManyFailuresPausedReason : null);
    }

    private async Task<(DateTimeOffset NextRunAt, int ConsecutiveFailures, string? PausedReason, string Cadence)?>
        LoadScheduleAsync(Guid identitySub, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT next_run_at, consecutive_failures, paused_reason, cadence
            FROM user_refresh_schedules
            WHERE identity_sub = @identity_sub
            """;
        cmd.AddParam(IdentitySubParameter, identitySub);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return (
            reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3));
    }

    private async Task<(Guid RunId, string Status, string? ErrorCode)?> LoadLatestRunAsync(Guid identitySub, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, status, error_code
            FROM job_runs
            WHERE identity_sub = @identity_sub AND kind = @kind
            ORDER BY created_at DESC
            LIMIT 1
            """;
        cmd.AddParam(IdentitySubParameter, identitySub);
        cmd.AddParam(CuratorSqlParameters.Kind, JobRunKinds.LibraryRefresh);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return (
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async Task DispatchLibraryRefreshAsync(Guid identitySub, CancellationToken ct)
    {
        var runId = Guid.CreateVersion7(DateTimeOffset.UtcNow);
        await _auditRepository.RecordAsync(
            identitySub,
            AccountActionLogRepository.LibraryRefreshRequested,
            runId.ToString(),
            token => InsertAndSendLibraryRefreshAsync(identitySub, runId, token),
            static _ => null,
            ct);
    }

    private async Task<Guid> InsertAndSendLibraryRefreshAsync(Guid identitySub, Guid runId, CancellationToken ct)
    {
        await using (var cmd = _dbConnection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO job_runs (run_id, kind, identity_sub) VALUES (@run_id, @kind, @identity_sub)
                """;
            cmd.AddParam(CuratorSqlParameters.RunId, runId);
            cmd.AddParam(CuratorSqlParameters.Kind, JobRunKinds.LibraryRefresh);
            cmd.AddParam(IdentitySubParameter, identitySub);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var sender = _serviceBusClient.CreateSender(LibraryRefreshQueuePublisher.Queue);
        var body = BinaryData.FromObjectAsJson(new LibraryRefreshMessage
        {
            RunId = runId,
            IdentitySub = identitySub,
        });
        await sender.SendMessageAsync(new ServiceBusMessage(body), ct);
        return runId;
    }

    private async Task PublishNextTickAsync(Guid identitySub, DateTimeOffset nextRunAt, CancellationToken ct)
    {
        await using var sender = _serviceBusClient.CreateSender(ScheduledRefreshQueue);
        var body = BinaryData.FromObjectAsJson(new ScheduledRefreshMessage(identitySub, nextRunAt));
        await sender.ScheduleMessageAsync(new ServiceBusMessage(body), nextRunAt, ct);
    }

    private async Task AdvanceScheduleAsync(
        Guid identitySub,
        DateTimeOffset lastRunAt,
        DateTimeOffset nextRunAt,
        int consecutiveFailures,
        CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            UPDATE user_refresh_schedules
            SET last_run_at = @last_run_at, next_run_at = @next_run_at, consecutive_failures = @consecutive_failures,
                paused_reason = NULL, updated_at = now()
            WHERE identity_sub = @identity_sub
            """;
        cmd.AddParam(CuratorSqlParameters.LastRunAt, lastRunAt);
        cmd.AddParam(CuratorSqlParameters.NextRunAt, nextRunAt);
        cmd.AddParam(CuratorSqlParameters.ConsecutiveFailures, consecutiveFailures);
        cmd.AddParam(IdentitySubParameter, identitySub);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task PauseScheduleAsync(
        Guid identitySub,
        DateTimeOffset lastRunAt,
        int consecutiveFailures,
        string pausedReason,
        CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            UPDATE user_refresh_schedules
            SET last_run_at = @last_run_at, consecutive_failures = @consecutive_failures,
                paused_reason = @paused_reason, updated_at = now()
            WHERE identity_sub = @identity_sub
            """;
        cmd.AddParam(CuratorSqlParameters.LastRunAt, lastRunAt);
        cmd.AddParam(CuratorSqlParameters.ConsecutiveFailures, consecutiveFailures);
        cmd.AddParam(CuratorSqlParameters.PausedReason, pausedReason);
        cmd.AddParam(IdentitySubParameter, identitySub);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
