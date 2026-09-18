namespace Functions.Curator.Rawg;

using Enrichment;

public sealed class RateLimitedRawgClient : IRawgClient
{
    private readonly IRawgClient _inner;
    private readonly IRawgRateLimiter _rateLimiter;

    public RateLimitedRawgClient(IRawgClient inner, IRawgRateLimiter rateLimiter)
    {
        _inner = inner;
        _rateLimiter = rateLimiter;
    }

    public async Task<IReadOnlyList<RawgCandidate>> SearchGamesAsync(
        string title,
        RawgCredential credential,
        int pageSize = RawgClient.DefaultSearchPageSize,
        CancellationToken cancellationToken = default)
    {
        await AcquireOrThrowAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.SearchGamesAsync(title, credential, pageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task ValidateKeyAsync(RawgCredential credential, CancellationToken cancellationToken = default)
    {
        await AcquireOrThrowAsync(cancellationToken).ConfigureAwait(false);
        await _inner.ValidateKeyAsync(credential, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RawgGameDetailResponse?> FetchDetailAsync(
        int rawgGameId,
        RawgCredential credential,
        CancellationToken cancellationToken = default)
    {
        await AcquireOrThrowAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.FetchDetailAsync(rawgGameId, credential, cancellationToken).ConfigureAwait(false);
    }

    private async Task AcquireOrThrowAsync(CancellationToken cancellationToken)
    {
        if (await _rateLimiter.TryAcquireAsync(cancellationToken).ConfigureAwait(false) is { } retryAfterSeconds)
        {
            throw new EnrichmentRateLimitException(EnrichmentProvider.Rawg, retryAfterSeconds);
        }
    }
}
