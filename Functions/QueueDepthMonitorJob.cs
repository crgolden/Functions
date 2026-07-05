namespace Functions;

using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;

// Nothing else watches dead-lettered messages today (no worker calls DeadLetterMessageAsync's
// counterpart consumer), so a poisoned message is invisible until someone looks. This polls each
// queue's runtime properties into a gauge so the "DLQ depth" alert rule has something to read.
public sealed class QueueDepthMonitorJob
{
    private static readonly string[] QueueNames =
    [
        "email",
        "scrape-requests",
        "extraction-requests",
        "enrichment-requests",
        "geocoding-requests",
        "confidence-requests",
        "contributions",
    ];

    private readonly ServiceBusAdministrationClient _adminClient;

    public QueueDepthMonitorJob(IAzureClientFactory<ServiceBusAdministrationClient> adminClientFactory) =>
        _adminClient = adminClientFactory.CreateClient("crgolden");

    [Function(nameof(QueueDepthMonitorJob))]
    public async Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken = default)
    {
        foreach (var queue in QueueNames)
        {
            try
            {
                var runtimeProperties = await _adminClient.GetQueueRuntimePropertiesAsync(queue, cancellationToken);
                Telemetry.Metrics.RecordQueueDepth(queue, runtimeProperties.Value.ActiveMessageCount, runtimeProperties.Value.DeadLetterMessageCount);
            }
            catch (RequestFailedException ex)
            {
                // Missing Manage claims (or any other admin-API failure) must not throw here — that
                // would feed the exceptions alert every 15 minutes. Degrade to a trace event instead.
                Telemetry.Tracing.RecordHandledFailure("servicebus.admin-auth-failed", $"{queue}: {ex.Message}");
            }
        }
    }
}
