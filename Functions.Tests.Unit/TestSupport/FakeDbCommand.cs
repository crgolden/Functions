namespace Functions.Tests.Unit.TestSupport;

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

internal sealed class FakeDbCommand : DbCommand
{
    private static readonly string[] AdvisoryLockFunctionNames =
    [
        Curator.AdvisoryLockHandle.TryAcquireFunctionName,
        Curator.AdvisoryLockHandle.ReleaseFunctionName,
        Curator.AdvisoryLockHandle.TransactionScopedFunctionName,
    ];

    private static readonly string AdvisoryLockAcquire = Curator.AdvisoryLockHandle.TryAcquireFunctionName;

    private static readonly object AdvisoryLockGranted = true;
    private static readonly object AdvisoryLockContended = false;

    private readonly FakeDbParameterCollection _parameters = new();
    private readonly Queue<FakeDbCommand>? _commandQueue;
    private readonly bool _grantsAdvisoryLocks;
    private readonly Lock _gate;
    private readonly Action<FakeDbCommand>? _onExecuted;
    private bool _resolved;
    private int _nonQueryResult;
    private object? _scalarResult;
    private DataTable? _readerTable;
    private bool _throwOnExecute;

    public FakeDbCommand()
        : this(null, grantsAdvisoryLocks: true, new Lock(), null)
    {
    }

    internal FakeDbCommand(
        Queue<FakeDbCommand>? commandQueue,
        bool grantsAdvisoryLocks,
        Lock gate,
        Action<FakeDbCommand>? onExecuted)
    {
        _commandQueue = commandQueue;
        _grantsAdvisoryLocks = grantsAdvisoryLocks;
        _gate = gate;
        _onExecuted = onExecuted;
    }

    public string? CapturedCommandText { get; private set; }

    public string ExecutedSql =>
        CapturedCommandText ?? throw new InvalidOperationException(
            "This command never had CommandText set, so it cannot have executed any SQL.");

    [AllowNull]
    public override string CommandText
    {
        get => CapturedCommandText
            ?? throw new InvalidOperationException($"No {nameof(CommandText)} was set on this fake.");
        set => CapturedCommandText = value;
    }

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public static FakeDbCommand WithNonQueryResult(int rowsAffected) => new() { _nonQueryResult = rowsAffected };

    public static FakeDbCommand WithScalarResult(object? value) => new() { _scalarResult = value };

    public static FakeDbCommand WithReader(DataTable table) => new() { _readerTable = table };

    public static FakeDbCommand ThatThrowsOnExecute() => new() { _throwOnExecute = true };

    public override void Cancel()
    {
    }

    public override void Prepare()
    {
    }

    public override int ExecuteNonQuery()
    {
        TakeConfiguredResult();
        if (_throwOnExecute)
        {
            throw new FakeDbException("The fake database rejected this command.");
        }

        return _nonQueryResult;
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ExecuteNonQuery());

    public override object? ExecuteScalar()
    {
        TakeConfiguredResult();
        if (_throwOnExecute)
        {
            throw new FakeDbException("The fake database rejected this command.");
        }

        if (IsAdvisoryLockAcquire())
        {
            return _grantsAdvisoryLocks ? AdvisoryLockGranted : AdvisoryLockContended;
        }

        return _scalarResult;
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ExecuteScalar());

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        TakeConfiguredResult();
        return (_readerTable ?? new DataTable()).CreateDataReader();
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        Task.FromResult(ExecuteDbDataReader(behavior));

    private bool IsAdvisoryLockAcquire() =>
        ExecutedSql.Contains(AdvisoryLockAcquire, StringComparison.Ordinal);

    private bool IsAdvisoryLockStatement() =>
        AdvisoryLockFunctionNames.Any(functionName => ExecutedSql.Contains(functionName, StringComparison.Ordinal));

    private void TakeConfiguredResult()
    {
        _onExecuted?.Invoke(this);
        if (_commandQueue is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            if (IsAdvisoryLockStatement() || _commandQueue.Count == 0)
            {
                return;
            }

            var configured = _commandQueue.Dequeue();
            _nonQueryResult = configured._nonQueryResult;
            _scalarResult = configured._scalarResult;
            _readerTable = configured._readerTable;
            _throwOnExecute = configured._throwOnExecute;
        }
    }
}
