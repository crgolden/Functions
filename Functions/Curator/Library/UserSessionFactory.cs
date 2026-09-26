namespace Functions.Curator.Library;

using System.Text;
using Functions.Curator.Enrichment;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;

public sealed class UserSessionFactory
{
    private readonly PsnLinkRepository _psnLinkRepository;
    private readonly EnrichmentKeysRepository _enrichmentKeysRepository;
    private readonly TokenCrypto _tokenCrypto;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPsnRateLimiter _psnRateLimiter;
    private readonly PsnAccessTokenCache _accessTokenCache;
    private readonly Telemetry _telemetry;

    public UserSessionFactory(
        PsnLinkRepository psnLinkRepository,
        EnrichmentKeysRepository enrichmentKeysRepository,
        TokenCrypto tokenCrypto,
        IHttpClientFactory httpClientFactory,
        IPsnRateLimiter psnRateLimiter,
        PsnAccessTokenCache accessTokenCache,
        Telemetry telemetry)
    {
        _psnLinkRepository = psnLinkRepository;
        _enrichmentKeysRepository = enrichmentKeysRepository;
        _tokenCrypto = tokenCrypto;
        _httpClientFactory = httpClientFactory;
        _psnRateLimiter = psnRateLimiter;
        _accessTokenCache = accessTokenCache;
        _telemetry = telemetry;
    }

    public Task<PsnSession> RestoreAsync(Guid identitySub, CancellationToken cancellationToken)
    {
        var tokenStore = new DbPsnTokenStore(identitySub, _psnLinkRepository, _tokenCrypto, _accessTokenCache);
        var psnHttpClient = _httpClientFactory.CreateClient(PsnSession.HttpClientName);
        return PsnSession.RestoreAsync(null, tokenStore, _psnRateLimiter, psnHttpClient, cancellationToken);
    }

    public async Task<EnrichmentCredentials> BuildCredentialsAsync(
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
