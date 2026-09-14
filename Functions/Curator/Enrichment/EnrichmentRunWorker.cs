namespace Functions.Curator.Enrichment;

using Azure.Messaging.ServiceBus;
using Catalog;
using Extensions;
using Jobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using OpenCritic;
using Psn;
using Rawg;

public sealed class EnrichmentRunWorker
{
    private const string EnrichmentQueue = "curator-enrichment";

    private readonly JobRunsRepository _jobRuns;
    private readonly CatalogRepository _catalogRepository;
    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly OpenCriticCacheRepository _openCriticCacheRepository;
    private readonly IRawgClient _rawgClient;
    private readonly IOpenCriticClient _openCriticClient;
    private readonly ICatalogClient _catalogClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPsnRateLimiter _psnRateLimiter;
    private readonly IRawgRateLimiterFactory _rawgRateLimiters;
    private readonly IReadOnlyList<string> _rawgApiKeys;
    private readonly IReadOnlyList<string> _openCriticRapidApiKeys;
    private readonly IReadOnlyList<string> _psnNpssoTokens;

    public EnrichmentRunWorker(
        JobRunsRepository jobRuns,
        CatalogRepository catalogRepository,
        EnrichmentRepository enrichmentRepository,
        OpenCriticCacheRepository openCriticCacheRepository,
        IRawgClient rawgClient,
        IOpenCriticClient openCriticClient,
        ICatalogClient catalogClient,
        IHttpClientFactory httpClientFactory,
        IPsnRateLimiter psnRateLimiter,
        IRawgRateLimiterFactory rawgRateLimiters,
        IConfiguration configuration)
    {
        _jobRuns = jobRuns;
        _catalogRepository = catalogRepository;
        _enrichmentRepository = enrichmentRepository;
        _openCriticCacheRepository = openCriticCacheRepository;
        _rawgClient = rawgClient;
        _openCriticClient = openCriticClient;
        _catalogClient = catalogClient;
        _httpClientFactory = httpClientFactory;
        _psnRateLimiter = psnRateLimiter;
        _rawgRateLimiters = rawgRateLimiters;
        _rawgApiKeys = configuration.ConfiguredValues(CuratorConfigurationKeys.RawgApiKey);
        _openCriticRapidApiKeys = configuration.ConfiguredValues(CuratorConfigurationKeys.OpenCriticRapidApiKey);
        _psnNpssoTokens = configuration.ConfiguredValues(CuratorConfigurationKeys.PsnNpsso);
    }

    [Function(nameof(EnrichmentRunWorker))]
    public Task Run(
        [ServiceBusTrigger(EnrichmentQueue, Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var runner = new LeasedJobRunner(_jobRuns);
        return runner.RunAsync<EnrichmentRunMessage>(
            message, messageActions, RunPassesAsync, cancellationToken);
    }

    private async Task<object?> RunPassesAsync(EnrichmentRunMessage payload, CancellationToken cancellationToken)
    {
        var psnHttpClient = _httpClientFactory.CreateClient(PsnSession.HttpClientName);
        var psnSessions = _psnNpssoTokens
            .Select(npsso => new PsnSession(npsso, tokenStore: null, _psnRateLimiter, psnHttpClient))
            .ToList();
        try
        {
            var enrichmentService = new EnrichmentOrchestrationService(
                new RateLimitedRawgClient(_rawgClient, _rawgRateLimiters.ForAdmin()),
                _openCriticClient,
                _catalogClient,
                _enrichmentRepository,
                _openCriticCacheRepository);
            var credentials = new EnrichmentCredentials
            {
                Rawg = _rawgApiKeys.Count == 0
                    ? null
                    : new RawgCredential { ApiKey = _rawgApiKeys[0] },
                Psn = psnSessions.Count == 0 ? null : new PsnSessionRotation(psnSessions),
            };

            return await EnrichmentRunProcessor.RunAsync(
                OpenCriticAdminRefresh(),
                enrichmentService,
                credentials,
                _catalogRepository,
                _enrichmentRepository,
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

    private OpenCriticAdminRefreshService? OpenCriticAdminRefresh() =>
        _openCriticRapidApiKeys.Count == 0
            ? null
            : new OpenCriticAdminRefreshService(
                _openCriticCacheRepository,
                _openCriticClient,
                [.. _openCriticRapidApiKeys.Select(key => new OpenCriticCredential { RapidApiKey = key })]);
}
