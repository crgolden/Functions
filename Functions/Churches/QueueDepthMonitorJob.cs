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
    private readonly Telemetry _telemetry;

    public QueueDepthMonitorJob(IAzureClientFactory<ServiceBusAdministrationClient> adminClientFactory, Telemetry telemetry)
    {
        _adminClient = adminClientFactory.CreateClient(AzureClientNames.Crgolden);
        _telemetry = telemetry;
    }

    [Function(nameof(QueueDepthMonitorJob))]
    public Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken = default) =>
        Task.WhenAll(QueueNames.Select(queue => RecordQueueDepthAsync(queue, cancellationToken)));

    private async Task RecordQueueDepthAsync(string queue, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Telemetry.Tracing.RecordHandledFailure("servicebus.monitor-cancelled", queue);
            return;
        }

        try
        {
            var runtimeProperties = await _adminClient.GetQueueRuntimePropertiesAsync(queue, cancellationToken);
            _telemetry.RecordQueueDepth(queue, runtimeProperties.Value.ActiveMessageCount, runtimeProperties.Value.DeadLetterMessageCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Telemetry.Tracing.RecordHandledFailure("servicebus.monitor-cancelled", queue);
        }
        catch (RequestFailedException ex)
        {
            Telemetry.Tracing.RecordHandledFailure("servicebus.admin-auth-failed", $"{queue}: {ex.Message}");
        }
    }
}
