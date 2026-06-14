namespace Functions.Tests.TestSupport;

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

internal sealed class FakeDbConnection : DbConnection
{
    private readonly Queue<FakeDbCommand> _commandQueue = new();
    private ConnectionState _state = ConnectionState.Closed;
    private string _connectionString = string.Empty;

    public List<FakeDbCommand> ExecutedCommands { get; } = [];

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

    public void Enqueue(FakeDbCommand cmd) => _commandQueue.Enqueue(cmd);

    public override void Open() => _state = ConnectionState.Open;

    public override void Close() => _state = ConnectionState.Closed;

    public override void ChangeDatabase(string databaseName)
    {
    }

    protected override DbCommand CreateDbCommand()
    {
        var cmd = _commandQueue.Count > 0 ? _commandQueue.Dequeue() : new FakeDbCommand();
        cmd.Connection = this;
        ExecutedCommands.Add(cmd);
        return cmd;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();
}

internal sealed class FakeDbCommand : DbCommand
{
    private readonly FakeDbParameterCollection _parameters = new();
    private int _nonQueryResult;
    private object? _scalarResult;
    private DataTable? _readerTable;

    public string? CapturedCommandText { get; private set; }

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

    public override void Cancel()
    {
    }

    public override void Prepare()
    {
    }

    public override int ExecuteNonQuery() => _nonQueryResult;

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_nonQueryResult);

    public override object? ExecuteScalar() => _scalarResult;

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_scalarResult);

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        (_readerTable ?? new DataTable()).CreateDataReader();

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        Task.FromResult<DbDataReader>((_readerTable ?? new DataTable()).CreateDataReader());
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
