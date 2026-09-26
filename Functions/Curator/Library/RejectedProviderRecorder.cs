namespace Functions.Curator.Library;

using Functions.Curator.Enrichment;

public sealed class RejectedProviderRecorder
{
    private readonly EnrichmentKeysRepository _enrichmentKeysRepository;
    private readonly AccountActionLogRepository _auditRepository;

    public RejectedProviderRecorder(
        EnrichmentKeysRepository enrichmentKeysRepository,
        AccountActionLogRepository auditRepository)
    {
        _enrichmentKeysRepository = enrichmentKeysRepository;
        _auditRepository = auditRepository;
    }

    public async Task RecordAsync(
        Guid identitySub,
        IReadOnlyList<EnrichmentProvider> rejectedProviders,
        CancellationToken cancellationToken)
    {
        foreach (var provider in rejectedProviders)
        {
            switch (provider)
            {
                case EnrichmentProvider.Rawg:
                    await _enrichmentKeysRepository.MarkRawgKeyRejectedAsync(identitySub, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case EnrichmentProvider.OpenCritic:
                    await _enrichmentKeysRepository.MarkOpenCriticKeyRejectedAsync(identitySub, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case EnrichmentProvider.Psn:
                    continue;
                default:
                    throw new ArgumentOutOfRangeException(nameof(rejectedProviders));
            }

            await _auditRepository
                .LogAsync(
                    identitySub,
                    AccountActionLogRepository.EnrichmentKeyRejected,
                    provider.ToWireName(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
