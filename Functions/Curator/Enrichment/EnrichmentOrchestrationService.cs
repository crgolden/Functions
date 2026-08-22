namespace Functions.Curator.Enrichment;

using System.Globalization;
using System.Text.Json;
using OpenCritic;
using Psn;
using Rawg;

public sealed class EnrichmentOrchestrationService
{
    internal const int TransportFailureLimit = 3;
    internal const string EsrbAuthority = "ESRB";
    internal const string RawgOnlyScoreSource = "RAWG Only";
    internal const string OpenCriticOnlyScoreSource = "OC Only";
    internal const string RawgAndOpenCriticScoreSource = "RAWG + OC";
    private const int RateLimitStatusCode = 429;
    private const int OpenCriticTopupMaxPages = 5;

    private static readonly int[] AuthFailureStatusCodes = [401, 403];
    private static readonly string[] MultiplayerKeywords = ["multiplayer", "co-op", "online", "pvp", "cooperative"];
    private static readonly string[] OpenCriticTopupPlatforms = ["ps4", "ps5"];

    private readonly IRawgClient _rawgClient;
    private readonly IOpenCriticClient _openCriticClient;
    private readonly ICatalogClient _catalogClient;
    private readonly EnrichmentRepository _repository;
    private readonly OpenCriticCacheRepository _openCriticCacheRepository;
    private readonly Dictionary<EnrichmentProvider, RateLimitBackoff> _rateLimitBackoffs;
    private readonly Dictionary<EnrichmentProvider, int> _consecutiveTransportFailures = [];

    private OpenCriticNameIndex? _openCriticNameIndex;
    private bool _openCriticTopupAttempted;

    public EnrichmentOrchestrationService(
        IRawgClient rawgClient,
        IOpenCriticClient openCriticClient,
        ICatalogClient catalogClient,
        EnrichmentRepository repository,
        OpenCriticCacheRepository openCriticCacheRepository)
        : this(
            rawgClient,
            openCriticClient,
            catalogClient,
            repository,
            openCriticCacheRepository,
            new Dictionary<EnrichmentProvider, double>())
    {
    }

    public EnrichmentOrchestrationService(
        IRawgClient rawgClient,
        IOpenCriticClient openCriticClient,
        ICatalogClient catalogClient,
        EnrichmentRepository repository,
        OpenCriticCacheRepository openCriticCacheRepository,
        IReadOnlyDictionary<EnrichmentProvider, double> rateLimitBackoffSeconds)
    {
        _rawgClient = rawgClient;
        _openCriticClient = openCriticClient;
        _catalogClient = catalogClient;
        _repository = repository;
        _openCriticCacheRepository = openCriticCacheRepository;
        _rateLimitBackoffs = rateLimitBackoffSeconds.ToDictionary(
            seed => seed.Key,
            seed => new RateLimitBackoff(seed.Value));
    }

    public bool OpencriticTopupIncomplete { get; private set; }

    public HashSet<EnrichmentProvider> TransportUnavailableProviders { get; } = [];

    public async Task<EnrichmentResult> EnrichGameAsync(
        string title,
        string? titleId,
        IReadOnlyDictionary<string, int> genrePriorities,
        PublisherTierRuleSet publisherTierRules,
        EnrichmentCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var rawgLookup = await ResolveRawgAsync(title, credentials.Rawg, cancellationToken);
        var rawgDetail = rawgLookup.Detail;
        var psnCatalog = await ResolvePsnCatalogAsync(titleId, credentials.Psn, cancellationToken);

        var rawgGenres = Names(rawgDetail, detail => detail.Genres);
        var (genre, subgenre) = GenreReconciliationService.ReconcileGenres(psnCatalog.Genres, rawgGenres, genrePriorities);

        var developers = Names(rawgDetail, detail => detail.Developers);
        var publishers = Names(rawgDetail, detail => detail.Publishers);
        var developer = developers.Count > 0 ? developers[0] : null;
        var publisher = OrFallback(psnCatalog.Publisher, publishers.Count > 0 ? publishers[0] : null);

        var aaaTier = publisherTierRules.ClassifyTier(publisher)
            ?? publisherTierRules.ClassifyTier(developer);

        var tags = Names(rawgDetail, detail => detail.Tags).Select(tag => tag.ToLowerInvariant()).ToList();
        bool? rawgMultiplayer = tags.Count == 0
            ? null
            : tags.Any(tag => MultiplayerKeywords.Any(keyword => tag.Contains(keyword, StringComparison.Ordinal)));
        var multiplayer = psnCatalog.Multiplayer ?? rawgMultiplayer;

        var metacritic = rawgDetail?.Metacritic;
        var criticalScore = metacritic is { } metacriticValue && metacriticValue != 0 ? metacriticValue : (double?)null;

        var ocGame = await ResolveOpenCriticAsync(title, credentials.OpenCritic, cancellationToken);

        var releaseYear = ReleaseYear.FromDate(psnCatalog.ReleaseDate)
            ?? ReleaseYear.FromText(rawgDetail?.Released);

        var esrb = OrFallback(EsrbRating(psnCatalog), rawgDetail?.EsrbRating?.Name);

        return new EnrichmentResult(
            genre,
            subgenre,
            releaseYear,
            developer,
            publisher,
            esrb,
            multiplayer,
            criticalScore,
            ocGame?.TopCriticScore,
            ocGame?.Tier,
            ocGame?.PercentRecommended,
            psnCatalog.StarRating,
            ScoreSource(criticalScore, ocGame?.TopCriticScore),
            aaaTier,
            rawgDetail is not null,
            ocGame is not null,
            rawgLookup.Attempted);
    }

    private static string? ScoreSource(double? criticalScore, double? ocScore)
    {
        if (criticalScore is not null && ocScore is not null)
        {
            return RawgAndOpenCriticScoreSource;
        }

        if (ocScore is not null)
        {
            return OpenCriticOnlyScoreSource;
        }

        return criticalScore is not null ? RawgOnlyScoreSource : null;
    }

    private static string? OrFallback(string? primary, string? fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    private static string? EsrbRating(PsnCatalogLookup psnCatalog) =>
        string.Equals(psnCatalog.RatingAuthority, EsrbAuthority, StringComparison.Ordinal)
            ? psnCatalog.ContentRating
            : null;

    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static IReadOnlyList<string> Names(
        RawgGameDetail? detail,
        Func<RawgGameDetail, IReadOnlyList<RawgNamed>> selector) =>
        detail is null ? [] : detail.NamesOf(selector);

    private void NoteTransportFailure(EnrichmentProvider provider)
    {
        var failures = _consecutiveTransportFailures.GetValueOrDefault(provider) + 1;
        _consecutiveTransportFailures[provider] = failures;
        if (failures >= TransportFailureLimit)
        {
            TransportUnavailableProviders.Add(provider);
        }
    }

    private void NoteTransportSuccess(EnrichmentProvider provider) => _consecutiveTransportFailures.Remove(provider);

    private double RateLimitRetryAfter(EnrichmentProvider provider, double? hintedSeconds)
    {
        if (!_rateLimitBackoffs.TryGetValue(provider, out var backoff))
        {
            backoff = new RateLimitBackoff();
            _rateLimitBackoffs[provider] = backoff;
        }

        return backoff.RetryAfter(hintedSeconds);
    }

    private async Task<RawgLookup> ResolveRawgAsync(
        string title,
        RawgCredential? credential,
        CancellationToken cancellationToken)
    {
        if (credential is null || TransportUnavailableProviders.Contains(EnrichmentProvider.Rawg))
        {
            return RawgLookup.NeverAsked;
        }

        var cached = await _repository.GetRawgCacheAsync(title, cancellationToken);
        if (cached is not null)
        {
            return RawgLookup.Answered(
                cached.Raw is null ? null : JsonSerializer.Deserialize<RawgGameDetail>(cached.Raw));
        }

        IReadOnlyList<RawgCandidate> candidates;
        try
        {
            candidates = await _rawgClient.SearchGamesAsync(
                title, credential, cancellationToken: cancellationToken);
        }
        catch (RawgApiException exc) when (AuthFailureStatusCodes.Contains(exc.StatusCode))
        {
            throw new EnrichmentAuthException(EnrichmentProvider.Rawg, exc.Message);
        }
        catch (RawgApiException exc) when (exc.StatusCode == RateLimitStatusCode)
        {
            throw new EnrichmentRateLimitException(EnrichmentProvider.Rawg, RateLimitRetryAfter(EnrichmentProvider.Rawg, exc.RetryAfterSeconds));
        }
        catch (RawgApiException)
        {
            NoteTransportSuccess(EnrichmentProvider.Rawg);
            return RawgLookup.NeverAsked;
        }
        catch (Exception exc) when (IsTransportFailure(exc, cancellationToken))
        {
            NoteTransportFailure(EnrichmentProvider.Rawg);
            return RawgLookup.NeverAsked;
        }

        NoteTransportSuccess(EnrichmentProvider.Rawg);
        var match = RawgMatcher.FindBestMatch(title, candidates);
        if (match is null)
        {
            await _repository.SaveRawgCacheAsync(title, null, null, cancellationToken);
            return RawgLookup.Answered(null);
        }

        RawgGameDetailResponse? detail;
        try
        {
            detail = await _rawgClient.FetchDetailAsync(
                match.RawgGameId, credential, cancellationToken);
        }
        catch (RawgApiException exc) when (AuthFailureStatusCodes.Contains(exc.StatusCode))
        {
            throw new EnrichmentAuthException(EnrichmentProvider.Rawg, exc.Message);
        }
        catch (RawgApiException exc) when (exc.StatusCode == RateLimitStatusCode)
        {
            throw new EnrichmentRateLimitException(EnrichmentProvider.Rawg, RateLimitRetryAfter(EnrichmentProvider.Rawg, exc.RetryAfterSeconds));
        }
        catch (RawgApiException)
        {
            NoteTransportSuccess(EnrichmentProvider.Rawg);
            return RawgLookup.NeverAsked;
        }
        catch (Exception exc) when (IsTransportFailure(exc, cancellationToken))
        {
            NoteTransportFailure(EnrichmentProvider.Rawg);
            return RawgLookup.NeverAsked;
        }

        NoteTransportSuccess(EnrichmentProvider.Rawg);
        await _repository.SaveRawgCacheAsync(title, match.RawgGameId, detail?.Raw, cancellationToken);
        return RawgLookup.Answered(detail?.Detail);
    }

    private async Task<OpenCriticGame?> ResolveOpenCriticAsync(
        string title,
        OpenCriticCredential? credential,
        CancellationToken cancellationToken)
    {
        var match = await MatchOpenCriticCacheAsync(title, cancellationToken);
        if (match is not null)
        {
            return match;
        }

        if (credential is not null
            && !_openCriticTopupAttempted
            && !TransportUnavailableProviders.Contains(EnrichmentProvider.OpenCritic))
        {
            _openCriticTopupAttempted = true;
            await RunOpenCriticTopupAsync(credential, cancellationToken);
            return await MatchOpenCriticCacheAsync(title, cancellationToken);
        }

        return match;
    }

    private async Task<OpenCriticGame?> MatchOpenCriticCacheAsync(string title, CancellationToken cancellationToken)
    {
        _openCriticNameIndex ??= OpenCriticNameIndex.Build(
            await _repository.GetAllOpenCriticGamesAsync(cancellationToken));
        return _openCriticNameIndex.FindMatch(title);
    }

    private async Task RunOpenCriticTopupAsync(
        OpenCriticCredential credential,
        CancellationToken cancellationToken)
    {
        foreach (var platform in OpenCriticTopupPlatforms)
        {
            var startSkip = await _openCriticCacheRepository.GetCursorAsync(platform, cancellationToken);
            OpenCriticPaginationResult result;
            try
            {
                result = await _openCriticClient.FetchPlatformGamesAsync(
                    platform, credential, startSkip, OpenCriticTopupMaxPages, cancellationToken);
            }
            catch (OpenCriticApiException exc) when (AuthFailureStatusCodes.Contains(exc.StatusCode))
            {
                throw new EnrichmentAuthException(EnrichmentProvider.OpenCritic, exc.Message);
            }
            catch (OpenCriticApiException exc) when (exc.StatusCode == RateLimitStatusCode)
            {
                throw new EnrichmentRateLimitException(
                    EnrichmentProvider.OpenCritic,
                    RateLimitRetryAfter(EnrichmentProvider.OpenCritic, exc.RetryAfterSeconds));
            }
            catch (OpenCriticApiException)
            {
                OpencriticTopupIncomplete = true;
                return;
            }
            catch (OpenCriticNetworkException)
            {
                OpencriticTopupIncomplete = true;
                return;
            }
            catch (Exception exc) when (IsTransportFailure(exc, cancellationToken))
            {
                OpencriticTopupIncomplete = true;
                return;
            }

            await _openCriticCacheRepository.SaveGamesAsync(result.Games, cancellationToken);
            _openCriticNameIndex = null;
            await _openCriticCacheRepository.SetCursorAsync(platform, result.NextSkip, cancellationToken);
            if (!result.Exhausted)
            {
                OpencriticTopupIncomplete = true;
            }
        }
    }

    private async Task<PsnCatalogLookup> ResolvePsnCatalogAsync(
        string? titleId,
        PsnSessionRotation? rotation,
        CancellationToken cancellationToken)
    {
        if (titleId is null
            || rotation is null
            || TransportUnavailableProviders.Contains(EnrichmentProvider.Psn))
        {
            return new PsnCatalogLookup([], null);
        }

        var cached = await _repository.GetPsnCatalogCacheAsync(titleId, cancellationToken);
        if (cached is not null && cached.ConceptFetchedAt is not null)
        {
            return new PsnCatalogLookup(
                cached.Genres,
                cached.StarRating,
                cached.Publisher,
                cached.ReleaseDate,
                cached.ContentRating,
                cached.RatingAuthority,
                cached.Multiplayer);
        }

        TitleConcept concept;
        try
        {
            concept = await rotation.RunAsync(
                session => _catalogClient.TitleConceptAsync(session, titleId, cancellationToken));
        }
        catch (Exception exc) when (IsTransportFailure(exc, cancellationToken))
        {
            return new PsnCatalogLookup([], null);
        }

        DateOnly? releaseDate = concept.ReleaseDate is { } released
            ? DateOnly.FromDateTime(released.UtcDateTime)
            : null;
        await _repository.SavePsnCatalogCacheAsync(
            new PsnCatalogCacheEntry(
                titleId,
                concept.ConceptId,
                concept.Genres,
                concept.StarRating,
                concept.Publisher,
                releaseDate,
                concept.CoverImageUrl,
                concept.ContentRating,
                concept.RatingAuthority,
                concept.Multiplayer),
            cancellationToken);

        return new PsnCatalogLookup(
            concept.Genres,
            concept.StarRating,
            concept.Publisher,
            releaseDate,
            concept.ContentRating,
            concept.RatingAuthority,
            concept.Multiplayer);
    }
}
