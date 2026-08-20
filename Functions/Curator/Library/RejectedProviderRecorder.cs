namespace Functions.Curator.Library;

using Enrichment;

internal static class RejectedProviderRecorder
{
    private const string AuditWriteFailedEvent = "curator.library.audit-write-failed";

    public static async Task RecordAsync(
        string identitySub,
        IReadOnlyList<EnrichmentProvider> rejectedProviders,
        EnrichmentKeysRepository enrichmentKeysRepository,
        AccountActionLogRepository auditRepository,
        CancellationToken cancellationToken)
    {
        foreach (var provider in rejectedProviders)
        {
            switch (provider)
            {
                case EnrichmentProvider.Rawg:
                    await enrichmentKeysRepository.MarkRawgKeyRejectedAsync(identitySub, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case EnrichmentProvider.OpenCritic:
                    await enrichmentKeysRepository.MarkOpenCriticKeyRejectedAsync(identitySub, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case EnrichmentProvider.Psn:
                    continue;
                default:
                    throw new ArgumentOutOfRangeException(nameof(rejectedProviders));
            }

            await LogRejectionAsync(identitySub, provider, auditRepository, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task LogRejectionAsync(
        string identitySub,
        EnrichmentProvider provider,
        AccountActionLogRepository auditRepository,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditRepository
                .LogAsync(
                    identitySub,
                    AccountActionLogRepository.EnrichmentKeyRejected,
                    provider.ToWireName(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Telemetry.Tracing.RecordHandledException(AuditWriteFailedEvent, exception);
        }
    }
}
