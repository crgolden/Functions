namespace Functions;

using Azure.Messaging.ServiceBus;
using Grpc.Core;
using Microsoft.Azure.Functions.Worker;

internal static class ServiceBusSettlement
{
    internal const string SettleLostEvent = "servicebus.settle-lost";

    private const string LockLostReasonSuffix = nameof(ServiceBusFailureReason.MessageLockLost) + ")";

    private static readonly string HostFaultPrefix = typeof(ServiceBusException).FullName + ":";

    internal static Task CompleteAsync(ServiceBusMessageActions actions, ServiceBusReceivedMessage message) =>
        SettleAsync(() => actions.CompleteMessageAsync(message, CancellationToken.None), SettleLostEvent);

    internal static Task AbandonAsync(ServiceBusMessageActions actions, ServiceBusReceivedMessage message) =>
        SettleAsync(() => actions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None), SettleLostEvent);

    internal static Task DeadLetterAsync(ServiceBusMessageActions actions, ServiceBusReceivedMessage message, string reason) =>
        SettleAsync(
            () => actions.DeadLetterMessageAsync(message, deadLetterReason: reason, cancellationToken: CancellationToken.None),
            SettleLostEvent);

    internal static async Task SettleAsync(Func<Task> settle, string lostEvent)
    {
        try
        {
            await settle();
        }
        catch (RpcException exception) when (IsLost(exception))
        {
            Telemetry.Tracing.RecordHandledException(lostEvent, exception);
        }
    }

    internal static bool IsLost(RpcException exception) =>
        exception.StatusCode == StatusCode.FailedPrecondition || IsLockLost(exception);

    private static bool IsLockLost(RpcException exception)
    {
        if (exception.StatusCode != StatusCode.Unknown)
        {
            return false;
        }

        var detail = exception.Status.Detail;
        var firstLineEnd = detail.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = firstLineEnd < 0 ? detail : detail[..firstLineEnd];
        return firstLine.StartsWith(HostFaultPrefix, StringComparison.Ordinal)
            && firstLine.Contains(LockLostReasonSuffix, StringComparison.Ordinal);
    }
}
