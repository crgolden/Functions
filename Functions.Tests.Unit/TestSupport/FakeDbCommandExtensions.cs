namespace Functions.Tests.Unit.TestSupport;

using System.Data.Common;

internal static class FakeDbCommandExtensions
{
    public static T ParameterValue<T>(this DbCommand command, string parameterName) =>
        Assert.IsType<T>(command.Parameters[parameterName].Value);
}
