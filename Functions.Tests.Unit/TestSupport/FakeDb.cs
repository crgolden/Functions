namespace Functions.Tests.Unit.TestSupport;

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

internal sealed class FakeDbDataSource : DbDataSource
{
    private readonly Queue<FakeDbCommand> _commandQueue = new();
    private readonly Lock _gate = new();
    private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

    public List<FakeDbCommand> ExecutedCommands { get; } = [];

    public List<FakeDbConnection> Connections { get; } = [];

    public int ConnectionsCreated => Connections.Count;

    public override string ConnectionString => string.Empty;

    public bool GrantsAdvisoryLocks { get; set; } = true;

    public void Enqueue(FakeDbCommand cmd)
    {
        lock (_gate)
        {
            _commandQueue.Enqueue(cmd);
        }
    }

    public Task WhenExecuted(string commandTextFragment)
    {
        lock (_gate)
        {
            if (ExecutedCommands.Any(cmd => Matches(cmd, commandTextFragment)))
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((commandTextFragment, signal));
            return signal.Task;
        }
    }

    protected override DbConnection CreateDbConnection()
    {
        var connection = new FakeDbConnection(_commandQueue, ExecutedCommands, _gate, Executed)
        {
            GrantsAdvisoryLocks = GrantsAdvisoryLocks,
        };
        lock (_gate)
        {
            Connections.Add(connection);
        }

        return connection;
    }

    private static bool Matches(FakeDbCommand cmd, string fragment) =>
        cmd.ExecutedSql.Contains(fragment, StringComparison.Ordinal);

    private void Executed(FakeDbCommand cmd)
    {
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                if (!Matches(cmd, _waiters[index].Fragment))
                {
                    continue;
                }

                ready.Add(_waiters[index].Signal);
                _waiters.RemoveAt(index);
            }
        }

        foreach (var signal in ready)
        {
            signal.TrySetResult();
        }
    }
}

internal sealed class FakeDbConnection : DbConnection
{
    private readonly Queue<FakeDbCommand> _commandQueue;
    private readonly Lock _gate;
    private readonly Action<FakeDbCommand>? _onExecuted;
    private ConnectionState _state = ConnectionState.Closed;
    private string _connectionString = string.Empty;

    public FakeDbConnection()
        : this(new Queue<FakeDbCommand>(), [], new Lock(), null)
    {
    }

    internal FakeDbConnection(
        Queue<FakeDbCommand> commandQueue,
        List<FakeDbCommand> executedCommands,
        Lock gate,
        Action<FakeDbCommand>? onExecuted)
    {
        _commandQueue = commandQueue;
        ExecutedCommands = executedCommands;
        _gate = gate;
        _onExecuted = onExecuted;
    }

    public List<FakeDbCommand> ExecutedCommands { get; }

    public bool GrantsAdvisoryLocks { get; set; } = true;

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? string.Empty;
    }

    public override ConnectionState State => _state;

    public override string Database => string.Empty;

    public override string DataSource => string.Empty;

    public override string ServerVersion => string.Empty;

    public void Enqueue(FakeDbCommand cmd)
    {
        lock (_gate)
        {
            _commandQueue.Enqueue(cmd);
        }
    }

    public override void Open() => _state = ConnectionState.Open;

    public override void Close() => _state = ConnectionState.Closed;

    public override void ChangeDatabase(string databaseName)
    {
    }

    protected override DbCommand CreateDbCommand()
    {
        var cmd = new FakeDbCommand(_commandQueue, GrantsAdvisoryLocks, _gate, _onExecuted) { Connection = this };
        lock (_gate)
        {
            ExecutedCommands.Add(cmd);
        }

        return cmd;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new FakeDbTransaction(this, isolationLevel);
}

internal sealed class FakeDbTransaction : DbTransaction
{
    private readonly FakeDbConnection _connection;

    public FakeDbTransaction(FakeDbConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    public int CommitCount { get; private set; }

    public int RollbackCount { get; private set; }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection DbConnection => _connection;

    public override void Commit() => CommitCount++;

    public override void Rollback() => RollbackCount++;
}

internal sealed class FakeDbCommand : DbCommand
{
    private const string AdvisoryLockStatement = "advisory";
    private static readonly string AdvisoryLockAcquire = Functions.Curator.AdvisoryLockHandle.TryAcquireFunctionName;

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
        get => CapturedCommandText ?? string.Empty;
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
        ExecutedSql.Contains(AdvisoryLockStatement, StringComparison.Ordinal);

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

internal sealed class FakeDbException : DbException
{
    public FakeDbException(string message)
        : base(message)
    {
    }
}

internal sealed class FakeDbParameter : DbParameter
{
    private string _parameterName = string.Empty;
    private string _sourceColumn = string.Empty;

    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = value ?? string.Empty;
    }

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? string.Empty;
    }

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _params = [];

    public override int Count => _params.Count;

    public override object SyncRoot => _params;

    public override bool IsFixedSize => false;

    public override bool IsReadOnly => false;

    public override bool IsSynchronized => false;

    public override int Add(object value)
    {
        _params.Add((DbParameter)value);
        return _params.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values)
        {
            _params.Add((DbParameter)v);
        }
    }

    public override void Clear() => _params.Clear();

    public override bool Contains(object value) => _params.Contains((DbParameter)value);

    public override bool Contains(string value) => _params.Any(p => p.ParameterName == value);

    public override void CopyTo(Array array, int index) =>
        ((ICollection)_params).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _params.GetEnumerator();

    public override int IndexOf(object value) => _params.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _params.FindIndex(p => p.ParameterName == parameterName);

    public override void Insert(int index, object value) => _params.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _params.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _params.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _params.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _params[index];

    protected override DbParameter GetParameter(string parameterName) =>
        _params.First(p => p.ParameterName == parameterName);

    protected override void SetParameter(int index, DbParameter value) => _params[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) =>
        _params[IndexOf(parameterName)] = value;
}