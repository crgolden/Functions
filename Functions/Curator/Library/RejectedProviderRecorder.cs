namespace Functions.Curator.Library;

using Functions.Curator.Enrichment;

internal static class RejectedProviderRecorder
{
    public static async Task RecordAsync(
        Guid identitySub,
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

            await auditRepository
                .LogAsync(
                    identitySub,
                    AccountActionLogRepository.EnrichmentKeyRejected,
                    provider.ToWireName(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
