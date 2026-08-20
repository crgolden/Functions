namespace Functions.Curator.OpenCritic;

using Enrichment;

public sealed class OpenCriticAdminRefreshService
{
    public const int AdminRefreshMaxPages = 20;

    private static readonly string[] DefaultPlatforms = ["ps4", "ps5"];
    private static readonly int[] RotateOnStatusCodes = [401, 403, 429];
    private static readonly int[] AuthFailureStatusCodes = [401, 403];

    private readonly OpenCriticCacheRepository _repository;
    private readonly IOpenCriticClient _client;
    private readonly IReadOnlyList<OpenCriticCredential> _credentials;
    private readonly int _maxPagesPerRun;

    private readonly RateLimitBackoff _rateLimitBackoff = new();

    public OpenCriticAdminRefreshService(
        OpenCriticCacheRepository repository,
        IOpenCriticClient client,
        IReadOnlyList<OpenCriticCredential> credentials,
        int maxPagesPerRun = AdminRefreshMaxPages)
    {
        if (credentials.Count == 0)
        {
            throw new ArgumentException(
                "OpenCriticAdminRefreshService requires at least one credential.", nameof(credentials));
        }

        _repository = repository;
        _client = client;
        _credentials = credentials;
        _maxPagesPerRun = maxPagesPerRun;
    }

    public Task<OpenCriticRefreshOutcome> RefreshCacheAsync(CancellationToken cancellationToken = default) =>
        RefreshCacheAsync(DefaultPlatforms, cancellationToken);

    public async Task<OpenCriticRefreshOutcome> RefreshCacheAsync(
        IReadOnlyList<string> platforms,
        CancellationToken cancellationToken = default)
    {
        var total = 0;
        var processedPlatformCount = 0;
        var contendedPlatforms = new List<string>();
        var credentials = _credentials;
        foreach (var platform in platforms)
        {
            await using var cursorLock = await _repository
                .TryLockCursorAsync(platform, cancellationToken)
                .ConfigureAwait(false);
            if (!cursorLock.Acquired)
            {
                contendedPlatforms.Add(platform);
                continue;
            }

            var (fetched, remaining) = await RefreshPlatformAsync(platform, credentials, cancellationToken)
                .ConfigureAwait(false);
            total += fetched;
            processedPlatformCount++;
            credentials = remaining;
        }

        return new OpenCriticRefreshOutcome(total, processedPlatformCount, contendedPlatforms);
    }

    private async Task<(int Fetched, IReadOnlyList<OpenCriticCredential> Remaining)> RefreshPlatformAsync(
        string platform,
        IReadOnlyList<OpenCriticCredential> credentials,
        CancellationToken cancellationToken)
    {
        OpenCriticApiException? lastRotatingException = null;
        for (var index = 0; index < credentials.Count; index++)
        {
            var credential = credentials[index];
            var startSkip = await _repository.GetCursorAsync(platform, cancellationToken).ConfigureAwait(false);
            OpenCriticPaginationResult result;
            try
            {
                result = await _client
                    .FetchPlatformGamesAsync(
                        platform, credential, startSkip, _maxPagesPerRun, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OpenCriticApiException exception)
            {
                if (exception.PartialGames is { Count: > 0 } partialGames)
                {
                    await _repository.SaveGamesAsync(partialGames, cancellationToken).ConfigureAwait(false);
                }

                if (exception.PartialNextSkip is { } partialNextSkip)
                {
                    await _repository.SetCursorAsync(platform, partialNextSkip, cancellationToken).ConfigureAwait(false);
                }

                if (RotateOnStatusCodes.Contains(exception.StatusCode))
                {
                    lastRotatingException = exception;
                    continue;
                }

                return (0, credentials.Skip(index).ToList());
            }
            catch (OpenCriticNetworkException exception)
            {
                await _repository.SaveGamesAsync(exception.PartialGames, cancellationToken).ConfigureAwait(false);
                await _repository.SetCursorAsync(platform, exception.PartialNextSkip, cancellationToken).ConfigureAwait(false);
                return (0, credentials.Skip(index).ToList());
            }

            await _repository.SaveGamesAsync(result.Games, cancellationToken).ConfigureAwait(false);
            await _repository.SetCursorAsync(platform, result.NextSkip, cancellationToken).ConfigureAwait(false);
            return (result.Games.Count, credentials.Skip(index).ToList());
        }

        if (lastRotatingException is null)
        {
            throw new InvalidOperationException(
                "OpenCriticAdminRefreshService requires at least one credential.");
        }

        if (AuthFailureStatusCodes.Contains(lastRotatingException.StatusCode))
        {
            throw new EnrichmentAuthException(EnrichmentProvider.OpenCritic, lastRotatingException.Message);
        }

        throw new EnrichmentRateLimitException(
            EnrichmentProvider.OpenCritic, _rateLimitBackoff.RetryAfter(lastRotatingException.RetryAfterSeconds));
    }
}
