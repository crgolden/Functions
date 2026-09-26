namespace Functions.Tests.Unit.TestSupport;

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

internal sealed class FakeDbConnection : DbConnection
{
    private readonly Queue<FakeDbCommand> _commandQueue;
    private readonly Lock _gate;
    private readonly Action<FakeDbCommand>? _onExecuted;
    private ConnectionState _state = ConnectionState.Closed;
    private string? _connectionString;

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
        get => _connectionString
            ?? throw new InvalidOperationException($"No {nameof(ConnectionString)} was set on this fake.");
        set => _connectionString = value;
    }

    public override ConnectionState State => _state;

    public override string Database => nameof(FakeDbConnection);

    public override string DataSource => nameof(FakeDbConnection);

    public override string ServerVersion => nameof(FakeDbConnection);

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
