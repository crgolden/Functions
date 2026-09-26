namespace Functions.Tests.Unit.TestSupport;

using System.Data;
using System.Data.Common;

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
