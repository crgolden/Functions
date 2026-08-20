namespace Functions.Curator.Jobs;

using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Functions.Extensions;
using Library;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class ScheduledRefreshWorker
{
    internal const string PsnLinkExpiredPausedReason = "psn-link-expired";
    internal const string TooManyFailuresPausedReason = "too-many-consecutive-failures";

    private const string ScheduledRefreshQueue = "curator-scheduled-refresh";

    private static readonly TimeSpan WeeklyInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan MonthlyInterval = TimeSpan.FromDays(30);
    private static readonly TimeSpan ScheduledForTolerance = TimeSpan.FromSeconds(1);
    private static readonly string[] TerminalStatuses = [JobRunStatuses.Succeeded, JobRunStatuses.Failed];

    private readonly DbConnection _dbConnection;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly int _maxConsecutiveFailures;

    public ScheduledRefreshWorker(
        [FromKeyedServices("Curator")] DbConnection dbConnection,
        IAzureClientFactory<ServiceBusClient> serviceBusClientFactory,
        IConfiguration configuration)
    {
        _dbConnection = dbConnection;
        _serviceBusClient = serviceBusClientFactory.CreateClient("crgolden");
        _maxConsecutiveFailures = configuration.GetValue<int?>("ScheduledRefreshMaxConsecutiveFailures") ?? 3;
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

        var latestRun = await LoadLatestRunAsync(payload.IdentitySub, cancellationToken);
        var consecutiveFailures = schedule.Value.ConsecutiveFailures;
        string? pausedReason = null;

        if (latestRun is null || TerminalStatuses.Contains(latestRun.Value.Status))
        {
            if (latestRun is { Status: JobRunStatuses.Failed } failedRun)
            {
                consecutiveFailures++;
                if (failedRun.ErrorCode == JobErrorCodes.PsnLinkExpired)
                {
                    pausedReason = PsnLinkExpiredPausedReason;
                }
                else if (consecutiveFailures >= _maxConsecutiveFailures)
                {
                    pausedReason = TooManyFailuresPausedReason;
                }
            }
            else
            {
                consecutiveFailures = 0;
            }

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
            var nextRunAt = now + (string.Equals(schedule.Value.Cadence, RefreshCadences.Monthly, StringComparison.Ordinal)
                ? MonthlyInterval
                : WeeklyInterval);
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

    private async Task<(DateTimeOffset NextRunAt, int ConsecutiveFailures, string? PausedReason, string Cadence)?>
        LoadScheduleAsync(Guid identitySub, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT next_run_at, consecutive_failures, paused_reason, cadence
            FROM user_refresh_schedules
            WHERE identity_sub = @identity_sub
            """;
        cmd.AddParam("@identity_sub", identitySub);
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
        cmd.AddParam("@identity_sub", identitySub);
        cmd.AddParam("@kind", JobRunKinds.LibraryRefresh);
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
        await using (var cmd = _dbConnection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO job_runs (run_id, kind, identity_sub) VALUES (@run_id, @kind, @identity_sub)
                """;
            cmd.AddParam("@run_id", runId);
            cmd.AddParam("@kind", JobRunKinds.LibraryRefresh);
            cmd.AddParam("@identity_sub", identitySub);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var sender = _serviceBusClient.CreateSender(LibraryRefreshQueuePublisher.Queue);
        var body = BinaryData.FromObjectAsJson(new LibraryRefreshMessage(runId, identitySub));
        await sender.SendMessageAsync(new ServiceBusMessage(body), ct);
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
        cmd.AddParam("@last_run_at", lastRunAt);
        cmd.AddParam("@next_run_at", nextRunAt);
        cmd.AddParam("@consecutive_failures", consecutiveFailures);
        cmd.AddParam("@identity_sub", identitySub);
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
        cmd.AddParam("@last_run_at", lastRunAt);
        cmd.AddParam("@consecutive_failures", consecutiveFailures);
        cmd.AddParam("@paused_reason", pausedReason);
        cmd.AddParam("@identity_sub", identitySub);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed record ScheduledRefreshMessage(
    [property: JsonPropertyName("identity_sub")] Guid IdentitySub,
    [property: JsonPropertyName("scheduled_for")] DateTimeOffset ScheduledFor);

public sealed record LibraryRefreshMessage(
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("identity_sub")] Guid IdentitySub);
