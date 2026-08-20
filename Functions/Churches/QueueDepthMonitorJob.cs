namespace Functions.Churches;

using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;

public sealed class QueueDepthMonitorJob
{
    internal static readonly string[] QueueNames =
    [
        ChurchQueueNames.Email,
        ChurchQueueNames.ScrapeRequests,
        ChurchQueueNames.ExtractionRequests,
        ChurchQueueNames.EnrichmentRequests,
        ChurchQueueNames.GeocodingRequests,
        ChurchQueueNames.ConfidenceRequests,
        ChurchQueueNames.Contributions,
    ];

    private readonly ServiceBusAdministrationClient _adminClient;

    public QueueDepthMonitorJob(IAzureClientFactory<ServiceBusAdministrationClient> adminClientFactory) =>
        _adminClient = adminClientFactory.CreateClient(AzureClientNames.Crgolden);

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
                Telemetry.Tracing.RecordHandledFailure("servicebus.admin-auth-failed", $"{queue}: {ex.Message}");
            }
        }
    }
}