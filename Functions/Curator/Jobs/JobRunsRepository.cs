namespace Functions.Curator.Jobs;

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Functions.Extensions;

public sealed class JobRunsRepository
{
    public const double DefaultLeaseSeconds = 120.0;

    public const double DefaultAbandonedAfterSeconds = 24 * 60 * 60;

    private readonly DbDataSource _dataSource;

    public JobRunsRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> TryBeginDeliveryAsync(
        string runId,
        int expectedSeq,
        double leaseSeconds = DefaultLeaseSeconds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE job_runs
            SET status = 'running', error = NULL, updated_at = now(),
                lease_expires_at = now() + make_interval(secs => @lease_seconds)
            WHERE run_id = @run_id AND seq = @expected_seq
              AND status NOT IN ('succeeded', 'failed', 'cancelled')
              AND (status <> 'running' OR lease_expires_at IS NULL OR lease_expires_at <= now())
            RETURNING run_id
            """;
        cmd.AddParam("@lease_seconds", leaseSeconds);
        cmd.AddParam("@run_id", Guid.Parse(runId));
        cmd.AddParam("@expected_seq", expectedSeq);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> RenewLeaseAsync(
        string runId,
        double leaseSeconds = DefaultLeaseSeconds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE job_runs SET lease_expires_at = now() + make_interval(secs => @lease_seconds)
            WHERE run_id = @run_id AND status = 'running'
            RETURNING run_id
            """;
        cmd.AddParam("@lease_seconds", leaseSeconds);
        cmd.AddParam("@run_id", Guid.Parse(runId));
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> TryMarkSucceededAsync(
        string runId,
        object? resultSummary,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE job_runs SET status = 'succeeded', result_summary = @result_summary::jsonb, updated_at = now(),
                lease_expires_at = NULL
            WHERE run_id = @run_id AND status = 'running'
            RETURNING run_id
            """;
        var json = resultSummary is null ? null : JsonSerializer.Serialize(resultSummary);
        cmd.AddParam("@result_summary", (object?)json ?? DBNull.Value);
        cmd.AddParam("@run_id", Guid.Parse(runId));
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<int?> TryMarkRateLimitedAsync(
        string runId,
        object resultSummary,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE job_runs SET status = 'rate_limited', result_summary = @result_summary::jsonb, seq = seq + 1,
                updated_at = now(), lease_expires_at = NULL
            WHERE run_id = @run_id AND status = 'running'
            RETURNING seq
            """;
        cmd.AddParam("@result_summary", JsonSerializer.Serialize(resultSummary));
        cmd.AddParam("@run_id", Guid.Parse(runId));
        var seq = await cmd.ExecuteScalarAsync(cancellationToken);
        return seq is null ? null : Convert.ToInt32(seq, CultureInfo.InvariantCulture);
    }

    public async Task<bool> TryMarkFailedAsync(
        string runId,
        JobFailure failure,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE job_runs SET status = 'failed', error = @error, error_code = @error_code,
                updated_at = now(), lease_expires_at = NULL
            WHERE run_id = @run_id AND status = 'running'
            RETURNING run_id
            """;
        cmd.AddParam("@error", failure.Message);
        cmd.AddParam("@error_code", failure.ErrorCode);
        cmd.AddParam("@run_id", Guid.Parse(runId));
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<string>> ReapExpiredLeasesAsync(
        string error,
        string errorCode,
        double abandonedAfterSeconds = DefaultAbandonedAfterSeconds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE job_runs
            SET status = 'failed', error = @error, error_code = @error_code, updated_at = now(),
                lease_expires_at = NULL
            WHERE status = 'running' AND (lease_expires_at IS NULL OR lease_expires_at <= now())
              AND updated_at <= now() - make_interval(secs => @abandoned_after_seconds)
            RETURNING run_id
            """;
        cmd.AddParam("@error", error);
        cmd.AddParam("@error_code", errorCode);
        cmd.AddParam("@abandoned_after_seconds", abandonedAfterSeconds);

        var reaped = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reaped.Add(reader.GetGuid(0).ToString());
        }

        return reaped;
    }

    public async Task<JobRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, kind, identity_sub, status, error, seq, result_summary FROM job_runs WHERE run_id = @run_id
            """;
        cmd.AddParam("@run_id", Guid.Parse(runId));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JobRun(
            reader.GetGuid(0).ToString(),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5))
        {
            ResultSummary = reader.IsDBNull(6) ? null : reader.GetString(6),
        };
    }
}
