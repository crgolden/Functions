namespace Functions.Tests.Unit.TestSupport;

using System.Runtime.ExceptionServices;
using Azure.Messaging.ServiceBus;
using Grpc.Core;

internal static class HostSettlementFaults
{
    public static RpcException LockLost() => Wrapping(ServiceBusFailureReason.MessageLockLost);

    public static RpcException Wrapping(ServiceBusFailureReason reason) =>
        AsHostThrowsIt(new ServiceBusException(Generated.NewErrorMessage(), reason));

    public static RpcException WrappingOnEntity(ServiceBusFailureReason reason, string entityPath) =>
        AsHostThrowsIt(new ServiceBusException(Generated.NewErrorMessage(), reason, entityPath));

    public static RpcException LockLostWithStackTrace() =>
        AsHostThrowsIt((ServiceBusException)ExceptionDispatchInfo.SetCurrentStackTrace(
            new ServiceBusException(Generated.NewErrorMessage(), ServiceBusFailureReason.MessageLockLost)));

    public static RpcException LockLostWithStatus(StatusCode statusCode) =>
        new(new Status(statusCode, LockLost().Status.Detail));

    public static RpcException LockTokenReleased() =>
        new(new Status(StatusCode.FailedPrecondition, Generated.NewErrorMessage()));

    public static RpcException WithDetail(string detail) => new(new Status(StatusCode.Unknown, detail));

    private static RpcException AsHostThrowsIt(ServiceBusException fault) =>
        new(new Status(StatusCode.Unknown, fault.ToString()));
}
