namespace Functions.Curator.Enrichment;

using Azure.Messaging.ServiceBus;
using Functions.Curator.Jobs;
using Microsoft.Azure.Functions.Worker;

public sealed class EnrichmentRunWorker
{
    private const string EnrichmentQueue = "curator-enrichment";

    private readonly LeasedJobRunner _runner;
    private readonly EnrichmentServiceFactory _enrichmentServiceFactory;
    private readonly AdminEnrichmentFactory _adminFactory;
    private readonly EnrichmentRunProcessor _processor;

    public EnrichmentRunWorker(
        LeasedJobRunner runner,
        EnrichmentServiceFactory enrichmentServiceFactory,
        AdminEnrichmentFactory adminFactory,
        EnrichmentRunProcessor processor)
    {
        _runner = runner;
        _enrichmentServiceFactory = enrichmentServiceFactory;
        _adminFactory = adminFactory;
        _processor = processor;
    }

    [Function(nameof(EnrichmentRunWorker))]
    public Task Run(
        [ServiceBusTrigger(EnrichmentQueue, Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync<EnrichmentRunMessage>(message, messageActions, RunPassesAsync, cancellationToken);

    private async Task<object?> RunPassesAsync(EnrichmentRunMessage payload, CancellationToken cancellationToken)
    {
        var psnSessions = _adminFactory.CreatePsnSessions();
        try
        {
            var enrichment = new EnrichmentContext(
                _enrichmentServiceFactory.ForAdmin(),
                _adminFactory.BuildCredentials(psnSessions));

            return await _processor.RunAsync(
                _adminFactory.OpenCriticAdminRefresh(),
                enrichment,
                new JobTimeBudget(),
                cancellationToken);
        }
        finally
        {
            foreach (var session in psnSessions)
            {
                await session.DisposeAsync();
            }
        }
    }
}
