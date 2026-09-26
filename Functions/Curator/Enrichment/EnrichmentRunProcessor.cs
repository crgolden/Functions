namespace Functions.Curator.Enrichment;

using Functions.Curator.Catalog;
using Functions.Curator.Jobs;
using Functions.Curator.OpenCritic;

public sealed class EnrichmentRunProcessor
{
    internal const string NotConfigured = "not_configured";
    internal const string Ok = "ok";
    internal const string Ran = "ran";
    internal const string SkippedUnchanged = "skipped_unchanged";
    internal const string SkippedConcurrentRefresh = "skipped_concurrent_refresh";

    private readonly CatalogRepository _catalogRepository;
    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly EnrichmentBatchProcessor _batchProcessor;

    public EnrichmentRunProcessor(
        CatalogRepository catalogRepository,
        EnrichmentRepository enrichmentRepository,
        EnrichmentBatchProcessor batchProcessor)
    {
        _catalogRepository = catalogRepository;
        _enrichmentRepository = enrichmentRepository;
        _batchProcessor = batchProcessor;
    }

    public async Task<EnrichmentRunSummary> RunAsync(
        OpenCriticAdminRefreshService? openCriticAdminRefresh,
        EnrichmentContext enrichment,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        var openCriticSummary = await RefreshOpenCriticCacheAsync(openCriticAdminRefresh, cancellationToken);
        var franchiseSummary = await ReclassifyFranchiseAsync(cancellationToken);
        var publisherTierRules = await _enrichmentRepository.ListPublisherTierRulesAsync(cancellationToken);
        var tierSummary = await ReclassifyTierAsync(publisherTierRules, cancellationToken);
        var enrichmentSummary = await EnrichUnenrichedGamesAsync(
            enrichment,
            openCriticAdminRefresh is not null,
            publisherTierRules,
            timeBudget,
            cancellationToken);

        return new EnrichmentRunSummary(openCriticSummary, franchiseSummary, tierSummary, enrichmentSummary);
    }

    private static async Task<OpenCriticRefreshPassSummary> RefreshOpenCriticCacheAsync(
        OpenCriticAdminRefreshService? openCriticAdminRefresh,
        CancellationToken cancellationToken)
    {
        if (openCriticAdminRefresh is null)
        {
            return new OpenCriticRefreshPassSummary(NotConfigured);
        }

        try
        {
            var outcome = await openCriticAdminRefresh.RefreshCacheAsync(cancellationToken: cancellationToken);
            return outcome.EveryPlatformContended
                ? new OpenCriticRefreshPassSummary(SkippedConcurrentRefresh)
                : new OpenCriticRefreshPassSummary(Ok, GamesFetched: outcome.GamesFetched);
        }
        catch (EnrichmentAuthException exception)
        {
            return new OpenCriticRefreshPassSummary(JobStoppedReasons.AuthError, Detail: exception.Message);
        }
        catch (EnrichmentRateLimitException exception)
        {
            return new OpenCriticRefreshPassSummary(
                JobStoppedReasons.RateLimited, RetryAfterSeconds: exception.RetryAfterSeconds);
        }
    }

    private static (string? Provider, string? Reason) StoppedBy(EnrichmentBatchResult batch, int remainingCount)
    {
        if (remainingCount <= 0)
        {
            return (null, null);
        }

        if (batch.RateLimitedProvider is { } rateLimitedProvider)
        {
            return (rateLimitedProvider.ToWireName(), JobStoppedReasons.RateLimited);
        }

        return batch.RejectedProviders.Count > 0
            ? (batch.RejectedProviders[^1].ToWireName(), JobStoppedReasons.AuthError)
            : (null, batch.StoppedReason);
    }

    private async Task<ReclassificationPassSummary> ReclassifyFranchiseAsync(CancellationToken cancellationToken)
    {
        var rules = await _catalogRepository.ListFranchiseRulesAsync(cancellationToken);
        var fingerprint = FranchiseAssigner.FingerprintFranchiseRules(rules);
        var previousFingerprint = await _catalogRepository.GetFranchiseRulesFingerprintAsync(cancellationToken);
        if (string.Equals(fingerprint, previousFingerprint, StringComparison.Ordinal))
        {
            return new ReclassificationPassSummary(SkippedUnchanged);
        }

        var updated = await _catalogRepository.ReclassifyFranchiseAsync(rules, cancellationToken);
        await _catalogRepository.SetFranchiseRulesFingerprintAsync(fingerprint, cancellationToken);
        return new ReclassificationPassSummary(Ran, updated);
    }

    private async Task<ReclassificationPassSummary> ReclassifyTierAsync(
        IReadOnlyList<PublisherTierRule> publisherTierRules,
        CancellationToken cancellationToken)
    {
        var fingerprint = PublisherTierClassifier.FingerprintPublisherTierRules(publisherTierRules);
        var previousFingerprint = await _enrichmentRepository.GetPublisherTierRulesFingerprintAsync(cancellationToken);
        if (string.Equals(fingerprint, previousFingerprint, StringComparison.Ordinal))
        {
            return new ReclassificationPassSummary(SkippedUnchanged);
        }

        var updated = await _enrichmentRepository.ReclassifyTierAsync(publisherTierRules, cancellationToken);
        await _enrichmentRepository.SetPublisherTierRulesFingerprintAsync(fingerprint, cancellationToken);
        return new ReclassificationPassSummary(Ran, updated);
    }

    private async Task<EnrichmentPassSummary> EnrichUnenrichedGamesAsync(
        EnrichmentContext enrichment,
        bool hasOpenCriticAdminClients,
        IReadOnlyList<PublisherTierRule> publisherTierRules,
        JobTimeBudget? timeBudget,
        CancellationToken cancellationToken)
    {
        var credentials = enrichment.Credentials;
        var openCriticConfigured = hasOpenCriticAdminClients || credentials.OpenCritic is not null;
        var providers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnrichmentProviderNames.Rawg] = credentials.Rawg is not null ? Ok : NotConfigured,
            [EnrichmentProviderNames.OpenCritic] = openCriticConfigured ? Ok : NotConfigured,
            [EnrichmentProviderNames.Psn] = credentials.Psn is not null ? Ok : NotConfigured,
        };

        var allGames = await _catalogRepository.ListAllGameIdsAndTitlesAsync(cancellationToken);
        var unenriched = (await _enrichmentRepository.GetEnrichmentNeedsAsync(
                allGames.Select(game => game.GameId).ToList(), cancellationToken))
            .DistinctBy(need => need.GameId, EqualityComparer<Guid>.Default)
            .ToDictionary(need => need.GameId, EqualityComparer<Guid>.Default);
        if (unenriched.Count == 0)
        {
            return new EnrichmentPassSummary(providers, 0, 0, 0, null, null, [], []);
        }

        await using var passLock = await _enrichmentRepository.TryLockCatalogEnrichmentPassAsync(cancellationToken);
        if (!passLock.Acquired)
        {
            return new EnrichmentPassSummary(
                providers, 0, 0, unenriched.Count, null, JobStoppedReasons.ConcurrentRun, [], []);
        }

        var candidates = allGames
            .Where(game => unenriched.ContainsKey(game.GameId))
            .Select(game => new EnrichmentCandidate(
                game.GameId, game.CanonicalTitle, null, game.TitleId, false, unenriched[game.GameId]))
            .ToList();

        var batch = await _batchProcessor.EnrichGamesAsync(
            enrichment,
            candidates,
            publisherTierRules,
            stopOnFirstProviderFailure: true,
            timeBudget: timeBudget,
            cancellationToken: cancellationToken);

        var remainingCount = batch.RemainingGameIds.Count;
        var (stoppedProvider, stoppedReason) = StoppedBy(batch, remainingCount);
        return new EnrichmentPassSummary(
            providers,
            batch.EnrichedCount + (remainingCount > 0 ? 1 : 0),
            batch.EnrichedCount,
            remainingCount,
            stoppedProvider,
            stoppedReason,
            batch.RejectedProviders.ToWireNames(),
            batch.UnavailableProviders.ToWireNames(),
            batch.RawgEnrichedTitles.Count,
            batch.OpenCriticEnrichedTitles.Count,
            batch.PsnEnrichedTitles.Count);
    }
}
