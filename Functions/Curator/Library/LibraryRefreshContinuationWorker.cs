namespace Functions.Curator.Library;

using Azure.Messaging.ServiceBus;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Microsoft.Azure.Functions.Worker;

public sealed class LibraryRefreshContinuationWorker
{
    private readonly LeasedJobRunner _runner;
    private readonly AccountActionLogRepository _auditRepository;
    private readonly UserSessionFactory _sessionFactory;
    private readonly EnrichmentServiceFactory _enrichmentServiceFactory;
    private readonly LibraryRefreshContinuationProcessor _processor;

    public LibraryRefreshContinuationWorker(
        LeasedJobRunner runner,
        AccountActionLogRepository auditRepository,
        UserSessionFactory sessionFactory,
        EnrichmentServiceFactory enrichmentServiceFactory,
        LibraryRefreshContinuationProcessor processor)
    {
        _runner = runner;
        _auditRepository = auditRepository;
        _sessionFactory = sessionFactory;
        _enrichmentServiceFactory = enrichmentServiceFactory;
        _processor = processor;
    }

    [Function(nameof(LibraryRefreshContinuationWorker))]
    public Task Run(
        [ServiceBusTrigger(
            LibraryRefreshQueuePublisher.ContinuationQueue,
            Connection = "ServiceBusConnection",
            AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync<LibraryRefreshContinuationMessage>(
            message, messageActions, RunForUserAsync, cancellationToken);

    private static Dictionary<EnrichmentProvider, double> PreviousBackoff(
        string? previousProvider,
        double previousRetryAfterSeconds) =>
        EnrichmentProviderNames.FromWireName(previousProvider) is { } provider
            ? new Dictionary<EnrichmentProvider, double>
            {
                [provider] = RateLimitBackoff.Next(previousRetryAfterSeconds),
            }
            : new Dictionary<EnrichmentProvider, double>();

    private Task<object?> RunForUserAsync(
        LibraryRefreshContinuationMessage payload,
        CancellationToken cancellationToken) =>
        _auditRepository.RecordAsync(
            payload.IdentitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            payload.RunId.ToString(),
            token => RunRecordedForUserAsync(payload, token),
            LibraryRefreshRunDetails.PlannedStop(payload.RunId),
            cancellationToken);

    private async Task<object?> RunRecordedForUserAsync(
        LibraryRefreshContinuationMessage payload,
        CancellationToken cancellationToken)
    {
        var timeBudget = new JobTimeBudget();
        var identitySub = payload.IdentitySub;

        await using var session = await _sessionFactory.RestoreAsync(identitySub, cancellationToken).ConfigureAwait(false);
        var credentials = await _sessionFactory
            .BuildCredentialsAsync(identitySub, session, cancellationToken)
            .ConfigureAwait(false);
        var enrichmentService = _enrichmentServiceFactory.ForUser(
            identitySub,
            PreviousBackoff(payload.Provider, payload.RetryAfterSeconds));

        return await _processor
            .RunAsync(
                payload.RunId,
                identitySub,
                payload.RemainingGameIds,
                new EnrichmentContext(enrichmentService, credentials),
                timeBudget,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
