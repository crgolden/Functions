namespace Functions.Curator.Library;

using System.Text;
using Azure.Messaging.ServiceBus;
using Functions.Curator.Catalog;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;
using Microsoft.Azure.Functions.Worker;

public sealed class LibraryRefreshWorker
{
    private readonly JobRunsRepository _jobRuns;
    private readonly PsnLinkRepository _psnLinkRepository;
    private readonly EnrichmentKeysRepository _enrichmentKeysRepository;
    private readonly AccountActionLogRepository _auditRepository;
    private readonly CatalogRepository _catalogRepository;
    private readonly LibraryRepository _libraryRepository;
    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly EntitlementPullRepository _entitlementPullRepository;
    private readonly OpenCriticCacheRepository _openCriticCacheRepository;
    private readonly TokenCrypto _tokenCrypto;
    private readonly IRawgClient _rawgClient;
    private readonly IOpenCriticClient _openCriticClient;
    private readonly ICatalogClient _catalogClient;
    private readonly IPsnLibraryClient _libraryClient;
    private readonly IPsnTrophyClient _trophyClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPsnRateLimiter _psnRateLimiter;
    private readonly IRawgRateLimiterFactory _rawgRateLimiters;
    private readonly PsnAccessTokenCache _accessTokenCache;
    private readonly LibraryRefreshQueuePublisher _continuationPublisher;
    private readonly Telemetry _telemetry;

    public LibraryRefreshWorker(
        JobRunsRepository jobRuns,
        PsnLinkRepository psnLinkRepository,
        EnrichmentKeysRepository enrichmentKeysRepository,
        AccountActionLogRepository auditRepository,
        CatalogRepository catalogRepository,
        LibraryRepository libraryRepository,
        EnrichmentRepository enrichmentRepository,
        EntitlementPullRepository entitlementPullRepository,
        OpenCriticCacheRepository openCriticCacheRepository,
        TokenCrypto tokenCrypto,
        IRawgClient rawgClient,
        IOpenCriticClient openCriticClient,
        ICatalogClient catalogClient,
        IPsnLibraryClient libraryClient,
        IPsnTrophyClient trophyClient,
        IHttpClientFactory httpClientFactory,
        IPsnRateLimiter psnRateLimiter,
        IRawgRateLimiterFactory rawgRateLimiters,
        PsnAccessTokenCache accessTokenCache,
        LibraryRefreshQueuePublisher continuationPublisher,
        Telemetry telemetry)
    {
        _jobRuns = jobRuns;
        _psnLinkRepository = psnLinkRepository;
        _enrichmentKeysRepository = enrichmentKeysRepository;
        _auditRepository = auditRepository;
        _catalogRepository = catalogRepository;
        _libraryRepository = libraryRepository;
        _enrichmentRepository = enrichmentRepository;
        _entitlementPullRepository = entitlementPullRepository;
        _openCriticCacheRepository = openCriticCacheRepository;
        _tokenCrypto = tokenCrypto;
        _rawgClient = rawgClient;
        _openCriticClient = openCriticClient;
        _catalogClient = catalogClient;
        _libraryClient = libraryClient;
        _trophyClient = trophyClient;
        _httpClientFactory = httpClientFactory;
        _psnRateLimiter = psnRateLimiter;
        _rawgRateLimiters = rawgRateLimiters;
        _accessTokenCache = accessTokenCache;
        _continuationPublisher = continuationPublisher;
        _telemetry = telemetry;
    }

    [Function(nameof(LibraryRefreshWorker))]
    public Task Run(
        [ServiceBusTrigger(
            LibraryRefreshQueuePublisher.Queue,
            Connection = "ServiceBusConnection",
            AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        var runner = new LeasedJobRunner(_jobRuns, _telemetry);
        return runner.RunAsync<LibraryRefreshMessage>(
            message, messageActions, RunForUserAsync, cancellationToken);
    }

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
        var runId = payload.RunId;
        var identitySub = payload.IdentitySub;

        var link = await _psnLinkRepository.GetLinkAsync(identitySub, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No PSN link for user {identitySub}; cannot refresh library.");

        var tokenStore = new DbPsnTokenStore(
            identitySub, _psnLinkRepository, _tokenCrypto, _accessTokenCache);
        var psnHttpClient = _httpClientFactory.CreateClient(PsnSession.HttpClientName);
        await using var session = await PsnSession
            .RestoreAsync(
                null,
                tokenStore,
                _psnRateLimiter,
                psnHttpClient,
                cancellationToken)
            .ConfigureAwait(false);

        var ingestionService = new IngestionService(_libraryClient, _entitlementPullRepository);
        var credentials = await BuildCredentialsAsync(identitySub, session, cancellationToken)
            .ConfigureAwait(false);
        var enrichmentService = new EnrichmentOrchestrationService(
            new RateLimitedRawgClient(_rawgClient, _rawgRateLimiters.ForUser(identitySub)),
            _openCriticClient,
            _catalogClient,
            _enrichmentRepository,
            _openCriticCacheRepository);
        var orchestrator = new LibraryBuildOrchestrator(
            ingestionService, _catalogRepository, _libraryRepository, _enrichmentRepository, enrichmentService, _telemetry);

        var publisherTierRules = await _enrichmentRepository
            .ListPublisherTierRulesAsync(cancellationToken)
            .ConfigureAwait(false);

        return await LibraryRefreshProcessor
            .RunAsync(
                runId,
                identitySub,
                orchestrator,
                enrichmentService,
                _enrichmentKeysRepository,
                _auditRepository,
                _jobRuns,
                _continuationPublisher,
                _trophyClient,
                session,
                link.HarvestTrophies ? session : null,
                publisherTierRules,
                credentials,
                timeBudget,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<EnrichmentCredentials> BuildCredentialsAsync(
        Guid identitySub,
        PsnSession session,
        CancellationToken cancellationToken)
    {
        var (rawgKeyEnc, openCriticKeyEnc) = await _enrichmentKeysRepository
            .GetDecryptedKeyMaterialAsync(identitySub, cancellationToken)
            .ConfigureAwait(false);

        return new EnrichmentCredentials
        {
            Rawg = rawgKeyEnc is null
                ? null
                : new RawgCredential { ApiKey = Encoding.UTF8.GetString(_tokenCrypto.Decrypt(rawgKeyEnc)) },
            OpenCritic = openCriticKeyEnc is null
                ? null
                : new OpenCriticCredential
                {
                    RapidApiKey = Encoding.UTF8.GetString(_tokenCrypto.Decrypt(openCriticKeyEnc)),
                },
            Psn = new PsnSessionRotation([session], _telemetry),
        };
    }
}
