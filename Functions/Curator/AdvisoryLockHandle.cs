namespace Functions.Curator;

using System.Data.Common;
using Functions.Extensions;

public sealed class AdvisoryLockHandle : IAsyncDisposable
{
    internal const string TryAcquireFunctionName = "pg_try_advisory_lock";

    internal const string ReleaseFunctionName = "pg_advisory_unlock";

    internal const string TransactionScopedFunctionName = "pg_advisory_xact_lock";

    private readonly DbConnection? _connection;
    private readonly int _lockClass;
    private readonly string? _lockKey;

    private AdvisoryLockHandle(DbConnection? connection, int lockClass, string? lockKey)
    {
        _connection = connection;
        _lockClass = lockClass;
        _lockKey = lockKey;
    }

    public bool Acquired => _connection is not null;

    public bool ReleasedBeforeClosing { get; private set; }

    public static AdvisoryLockHandle Contended() => new(null, 0, null);

    public static async Task<AdvisoryLockHandle> TryAcquireAsync(
        DbDataSource dataSource,
        int lockClass,
        string lockKey,
        CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        bool acquired;
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {TryAcquireFunctionName}(@lock_class, hashtext(@lock_key))";
            cmd.AddParam("@lock_class", lockClass);
            cmd.AddParam("@lock_key", lockKey);
            acquired = await cmd.ExecuteScalarAsync(cancellationToken) is true;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        if (acquired)
        {
            return new AdvisoryLockHandle(connection, lockClass, lockKey);
        }

        await connection.DisposeAsync();
        return Contended();
    }

    public static async Task HoldUntilCommitAsync(
        DbConnection connection,
        DbTransaction transaction,
        int lockClass,
        string lockKey,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT {TransactionScopedFunctionName}(@lock_class, hashtext(@lock_key))";
        cmd.AddParam("@lock_class", lockClass);
        cmd.AddParam("@lock_key", lockKey);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null || _lockKey is null)
        {
            return;
        }

        ReleasedBeforeClosing = await TryUnlockAsync(_connection, _lockClass, _lockKey);
        await _connection.DisposeAsync();
    }

    private static async Task<bool> TryUnlockAsync(DbConnection connection, int lockClass, string lockKey)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {ReleaseFunctionName}(@lock_class, hashtext(@lock_key))";
            cmd.AddParam("@lock_class", lockClass);
            cmd.AddParam("@lock_key", lockKey);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }
}
