namespace Functions.Tests.Unit.TestSupport;

using System.Data.Common;

internal sealed class FakeDbException : DbException
{
    private readonly bool _isTransient;

    public FakeDbException(string message)
        : base(message)
    {
    }

    public FakeDbException(bool isTransient)
        : base(nameof(FakeDbException)) => _isTransient = isTransient;

    public override bool IsTransient => _isTransient;
}
