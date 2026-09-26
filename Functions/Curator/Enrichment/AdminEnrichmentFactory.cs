namespace Functions.Curator.Enrichment;

using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;

public sealed class AdminEnrichmentFactory
{
    private readonly AdminProviderKeys _keys;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPsnRateLimiter _psnRateLimiter;
    private readonly OpenCriticCacheRepository _openCriticCacheRepository;
    private readonly IOpenCriticClient _openCriticClient;
    private readonly Telemetry _telemetry;

    public AdminEnrichmentFactory(
        AdminProviderKeys keys,
        IHttpClientFactory httpClientFactory,
        IPsnRateLimiter psnRateLimiter,
        OpenCriticCacheRepository openCriticCacheRepository,
        IOpenCriticClient openCriticClient,
        Telemetry telemetry)
    {
        _keys = keys;
        _httpClientFactory = httpClientFactory;
        _psnRateLimiter = psnRateLimiter;
        _openCriticCacheRepository = openCriticCacheRepository;
        _openCriticClient = openCriticClient;
        _telemetry = telemetry;
    }

    public List<PsnSession> CreatePsnSessions()
    {
        var psnHttpClient = _httpClientFactory.CreateClient(PsnSession.HttpClientName);
        return _keys.PsnNpssoTokens
            .Select(npsso => new PsnSession(npsso, tokenStore: null, _psnRateLimiter, psnHttpClient))
            .ToList();
    }

    public EnrichmentCredentials BuildCredentials(IReadOnlyList<PsnSession> psnSessions) => new()
    {
        Rawg = _keys.RawgApiKeys.Count == 0
            ? null
            : new RawgCredential { ApiKey = _keys.RawgApiKeys[0] },
        Psn = psnSessions.Count == 0 ? null : new PsnSessionRotation(psnSessions, _telemetry),
    };

    public OpenCriticAdminRefreshService? OpenCriticAdminRefresh() =>
        _keys.OpenCriticRapidApiKeys.Count == 0
            ? null
            : new OpenCriticAdminRefreshService(
                _openCriticCacheRepository,
                _openCriticClient,
                [.. _keys.OpenCriticRapidApiKeys.Select(key => new OpenCriticCredential { RapidApiKey = key })]);
}
