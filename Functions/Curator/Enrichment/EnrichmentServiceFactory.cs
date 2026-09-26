namespace Functions.Curator.Enrichment;

using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;

public sealed class EnrichmentServiceFactory
{
    private readonly IRawgClient _rawgClient;
    private readonly IOpenCriticClient _openCriticClient;
    private readonly ICatalogClient _catalogClient;
    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly OpenCriticCacheRepository _openCriticCacheRepository;
    private readonly IRawgRateLimiterFactory _rawgRateLimiters;

    public EnrichmentServiceFactory(
        IRawgClient rawgClient,
        IOpenCriticClient openCriticClient,
        ICatalogClient catalogClient,
        EnrichmentRepository enrichmentRepository,
        OpenCriticCacheRepository openCriticCacheRepository,
        IRawgRateLimiterFactory rawgRateLimiters)
    {
        _rawgClient = rawgClient;
        _openCriticClient = openCriticClient;
        _catalogClient = catalogClient;
        _enrichmentRepository = enrichmentRepository;
        _openCriticCacheRepository = openCriticCacheRepository;
        _rawgRateLimiters = rawgRateLimiters;
    }

    public EnrichmentOrchestrationService ForUser(Guid identitySub) =>
        new(
            new RateLimitedRawgClient(_rawgClient, _rawgRateLimiters.ForUser(identitySub)),
            _openCriticClient,
            _catalogClient,
            _enrichmentRepository,
            _openCriticCacheRepository);

    public EnrichmentOrchestrationService ForUser(
        Guid identitySub,
        IReadOnlyDictionary<EnrichmentProvider, double> rateLimitBackoffSeconds) =>
        new(
            new RateLimitedRawgClient(_rawgClient, _rawgRateLimiters.ForUser(identitySub)),
            _openCriticClient,
            _catalogClient,
            _enrichmentRepository,
            _openCriticCacheRepository,
            rateLimitBackoffSeconds);

    public EnrichmentOrchestrationService ForAdmin() =>
        new(
            new RateLimitedRawgClient(_rawgClient, _rawgRateLimiters.ForAdmin()),
            _openCriticClient,
            _catalogClient,
            _enrichmentRepository,
            _openCriticCacheRepository);
}
