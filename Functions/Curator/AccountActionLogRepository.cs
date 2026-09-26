namespace Functions.Curator;

using System.Data.Common;
using Functions.Extensions;

public sealed class AccountActionLogRepository
{
    public const string EnrichmentKeyRejected = "enrichment_key_rejected";

    public const string LibraryRefreshRequested = "library_refresh_requested";

    public const string LibraryRefreshRun = "library_refresh_run";

    public const string OutcomeStarted = "started";

    public const string OutcomeCompleted = "completed";

    public const string OutcomeFailed = "failed";

    public const string CancelledDetail = "cancelled";

    public const string OutcomeWriteFailedEvent = "curator.audit.outcome-write-failed";

    private readonly DbDataSource _dataSource;

    public AccountActionLogRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public static string Cancelled(string? startedDetail) =>
        startedDetail is null ? CancelledDetail : $"{startedDetail} {CancelledDetail}";

    public async Task LogAsync(
        Guid identitySub,
        string action,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO account_action_log (identity_sub, action, detail)
            VALUES (@identity_sub, @action, @detail)
            """;
        cmd.AddParam(CuratorSqlParameters.IdentitySub, identitySub);
        cmd.AddParam(CuratorSqlParameters.Action, action);
        cmd.AddParam(CuratorSqlParameters.Detail, detail);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> BeginAsync(
        Guid identitySub,
        string action,
        string? detail,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO account_action_log (identity_sub, action, detail, outcome)
            VALUES (@identity_sub, @action, @detail, @outcome)
            RETURNING log_id
            """;
        cmd.AddParam(CuratorSqlParameters.IdentitySub, identitySub);
        cmd.AddParam(CuratorSqlParameters.Action, action);
        cmd.AddParam(CuratorSqlParameters.Detail, detail);
        cmd.AddParam(CuratorSqlParameters.Outcome, OutcomeStarted);
        return await cmd.ExecuteScalarAsync(cancellationToken) is Guid logId
            ? logId
            : throw new InvalidOperationException($"account_action_log insert returned no log_id (action={action}).");
    }

    public async Task FinishAsync(Guid logId, string outcome, string? detail, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE account_action_log SET outcome = @outcome, detail = COALESCE(@detail, detail)
            WHERE log_id = @log_id AND outcome = @started
            """;
        cmd.AddParam(CuratorSqlParameters.Outcome, outcome);
        cmd.AddParam(CuratorSqlParameters.Detail, detail);
        cmd.AddParam(CuratorSqlParameters.LogId, logId);
        cmd.AddParam(CuratorSqlParameters.Started, OutcomeStarted);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"account_action_log row {logId} was not a started row; outcome not recorded.");
        }
    }

    public async Task<T> RecordAsync<T>(
        Guid identitySub,
        string action,
        string? detail,
        Func<CancellationToken, Task<T>> body,
        Func<Exception, string?> plannedStopDetail,
        CancellationToken cancellationToken)
    {
        var logId = await BeginAsync(identitySub, action, detail, cancellationToken);
        T result;
        try
        {
            result = await body(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await FinishKeepingTheBodyExceptionAsync(logId, OutcomeFailed, Cancelled(detail));
            throw;
        }
        catch (Exception exception)
        {
            var stoppedAsPlanned = plannedStopDetail(exception);
            await FinishKeepingTheBodyExceptionAsync(
                logId,
                stoppedAsPlanned is null ? OutcomeFailed : OutcomeCompleted,
                stoppedAsPlanned);
            throw;
        }

        await FinishAsync(logId, OutcomeCompleted, null, CancellationToken.None);
        return result;
    }

    private async Task FinishKeepingTheBodyExceptionAsync(Guid logId, string outcome, string? detail)
    {
        try
        {
            await FinishAsync(logId, outcome, detail, CancellationToken.None);
        }
        catch (Exception finishFailure)
        {
            Telemetry.Tracing.RecordHandledException(OutcomeWriteFailedEvent, finishFailure);
        }
    }
}
