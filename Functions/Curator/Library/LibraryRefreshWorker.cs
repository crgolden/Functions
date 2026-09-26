namespace Functions.Curator.Library;

using Azure.Messaging.ServiceBus;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.Psn;
using Microsoft.Azure.Functions.Worker;

public sealed class LibraryRefreshWorker
{
    private readonly LeasedJobRunner _runner;
    private readonly AccountActionLogRepository _auditRepository;
    private readonly PsnLinkRepository _psnLinkRepository;
    private readonly UserSessionFactory _sessionFactory;
    private readonly EnrichmentServiceFactory _enrichmentServiceFactory;
    private readonly LibraryRefreshProcessor _processor;

    public LibraryRefreshWorker(
        LeasedJobRunner runner,
        AccountActionLogRepository auditRepository,
        PsnLinkRepository psnLinkRepository,
        UserSessionFactory sessionFactory,
        EnrichmentServiceFactory enrichmentServiceFactory,
        LibraryRefreshProcessor processor)
    {
        _runner = runner;
        _auditRepository = auditRepository;
        _psnLinkRepository = psnLinkRepository;
        _sessionFactory = sessionFactory;
        _enrichmentServiceFactory = enrichmentServiceFactory;
        _processor = processor;
    }

    [Function(nameof(LibraryRefreshWorker))]
    public Task Run(
        [ServiceBusTrigger(
            LibraryRefreshQueuePublisher.Queue,
            Connection = "ServiceBusConnection",
            AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync<LibraryRefreshMessage>(message, messageActions, RunForUserAsync, cancellationToken);

    private Task<object?> RunForUserAsync(LibraryRefreshMessage payload, CancellationToken cancellationToken) =>
        _auditRepository.RecordAsync(
            payload.IdentitySub,
            AccountActionLogRepository.LibraryRefreshRun,
            payload.RunId.ToString(),
            token => RunRecordedForUserAsync(payload, token),
            LibraryRefreshRunDetails.PlannedStop(payload.RunId),
            cancellationToken);

    private async Task<object?> RunRecordedForUserAsync(LibraryRefreshMessage payload, CancellationToken cancellationToken)
    {
        var timeBudget = new JobTimeBudget();
        var identitySub = payload.IdentitySub;

        var link = await _psnLinkRepository.GetLinkAsync(identitySub, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No PSN link for user {identitySub}; cannot refresh library.");

        await using var session = await _sessionFactory.RestoreAsync(identitySub, cancellationToken).ConfigureAwait(false);
        var credentials = await _sessionFactory
            .BuildCredentialsAsync(identitySub, session, cancellationToken)
            .ConfigureAwait(false);
        var enrichment = new EnrichmentContext(_enrichmentServiceFactory.ForUser(identitySub), credentials);

        return await _processor
            .RunAsync(
                payload.RunId,
                identitySub,
                session,
                link.HarvestTrophies ? session : null,
                enrichment,
                timeBudget,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
