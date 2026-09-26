namespace Functions.Tests.Unit.TestSupport;

using System.Data.Common;

internal sealed class FakeDbDataSource : DbDataSource
{
    private readonly Queue<FakeDbCommand> _commandQueue = new();
    private readonly Lock _gate = new();
    private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

    public List<FakeDbCommand> ExecutedCommands { get; } = [];

    public List<FakeDbConnection> Connections { get; } = [];

    public int ConnectionsCreated => Connections.Count;

    public override string ConnectionString => nameof(FakeDbDataSource);

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
