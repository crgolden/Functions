namespace Functions.Curator.Enrichment;

using System.Diagnostics;
using Jobs;

public static class EnrichmentBatchProcessor
{
    public const int ProgressReportInterval = 25;

    private const string BatchStartedEvent = "curator.enrichment.batch-started";
    private const string BatchFinishedEvent = "curator.enrichment.batch-finished";
    private const string ProgressEvent = "curator.enrichment.progress";
    private const string TimeBudgetExpiredEvent = "curator.enrichment.time-budget-expired";
    private const string RateLimitedEvent = "curator.enrichment.provider-rate-limited";
    private const string KeyRejectedEvent = "curator.enrichment.provider-key-rejected";
    private const string RateLimitedReason = "rate-limited";
    private const string KeyRejectedReason = "key-rejected";

    public static async Task<EnrichmentBatchResult> EnrichGamesAsync(
        EnrichmentOrchestrationService enrichmentService,
        EnrichmentRepository enrichmentRepository,
        IReadOnlyList<EnrichmentCandidate> games,
        IReadOnlyList<PublisherTierRule> publisherTierRules,
        EnrichmentCredentials credentials,
        bool stopOnFirstProviderFailure = false,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        var live = credentials;
        var tierRules = PublisherTierRuleSet.Prepare(publisherTierRules);
        Telemetry.Tracing.RecordEvent(BatchStartedEvent, new ActivityTagsCollection { { "game.count", games.Count } });
        var genreRows = await enrichmentRepository.GetActiveGenresAsync(cancellationToken);
        var genrePriorities = new Dictionary<string, int>(StringComparer.Ordinal);
        var genreIdsByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in genreRows)
        {
            var lower = row.Name.ToLowerInvariant();
            genrePriorities[lower] = row.Priority;
            genreIdsByName[lower] = row.GenreId;
        }

        var enrichedCount = 0;
        var rawgEnrichedTitles = new List<string>();
        var openCriticEnrichedTitles = new List<string>();
        var rejectedProviders = new List<EnrichmentProvider>();
        var rateLimitBackoffs = new Dictionary<EnrichmentProvider, double>();
        int? resumeFromIndex = null;
        var timeBudgetExhausted = false;
        var index = 0;

        while (index < games.Count)
        {
            if (timeBudget?.Expired == true)
            {
                Telemetry.Tracing.RecordEvent(TimeBudgetExpiredEvent, new ActivityTagsCollection
                {
                    { "enriched.count", enrichedCount },
                    { "game.count", games.Count },
                    { "elapsed.minutes", timeBudget.Elapsed.TotalMinutes },
                });
                resumeFromIndex ??= index;
                timeBudgetExhausted = true;
                break;
            }

            var candidate = games[index];
            EnrichmentResult result;
            try
            {
                result = await enrichmentService.EnrichGameAsync(
                    candidate.Title,
                    candidate.TitleId,
                    genrePriorities,
                    tierRules,
                    live,
                    cancellationToken);
            }
            catch (EnrichmentRateLimitException exc)
            {
                Telemetry.Metrics.ProviderDisabled(exc.Provider.ToWireName(), RateLimitedReason);
                Telemetry.Tracing.RecordHandledException(RateLimitedEvent, exc);
                resumeFromIndex ??= index;
                var alreadyRateLimited = !rateLimitBackoffs.TryAdd(exc.Provider, exc.RetryAfterSeconds);
                if (stopOnFirstProviderFailure || alreadyRateLimited)
                {
                    break;
                }

                live = live.Without(exc.Provider);
                continue;
            }
            catch (EnrichmentAuthException exc)
            {
                Telemetry.Metrics.ProviderDisabled(exc.Provider.ToWireName(), KeyRejectedReason);
                Telemetry.Tracing.RecordHandledException(KeyRejectedEvent, exc);
                var alreadyRejected = rejectedProviders.Contains(exc.Provider);
                if (!alreadyRejected)
                {
                    rejectedProviders.Add(exc.Provider);
                }

                if (stopOnFirstProviderFailure || alreadyRejected)
                {
                    resumeFromIndex ??= index;
                    break;
                }

                live = live.Without(exc.Provider);
                continue;
            }

            var genreId = GenreId(genreIdsByName, result.Genre);
            var subgenreId = GenreId(genreIdsByName, result.Subgenre);
            await enrichmentRepository.SaveGameEnrichmentAsync(
                candidate.GameId,
                genreId,
                subgenreId,
                new GameEnrichmentSignals(
                    result.ReleaseYear,
                    result.Developer,
                    result.Publisher,
                    result.Esrb,
                    result.Multiplayer,
                    result.CriticalScore,
                    result.OcScore,
                    result.OcTier,
                    result.OcPercentRecommended,
                    result.PsnRating,
                    result.ScoreSource,
                    result.AaaTier,
                    result.RawgEnriched,
                    result.OpencriticEnriched,
                    result.RawgAttempted),
                cancellationToken);
            enrichedCount++;
            if (result.RawgEnriched)
            {
                rawgEnrichedTitles.Add(candidate.Title);
            }

            if (result.OpencriticEnriched)
            {
                openCriticEnrichedTitles.Add(candidate.Title);
            }

            index++;
            Telemetry.Metrics.GamesEnriched(1);
            if (enrichedCount % ProgressReportInterval == 0)
            {
                Telemetry.Tracing.RecordEvent(ProgressEvent, new ActivityTagsCollection
                {
                    { "enriched.count", enrichedCount },
                    { "game.count", games.Count },
                });
            }
        }

        Telemetry.Tracing.RecordEvent(BatchFinishedEvent, new ActivityTagsCollection
        {
            { "enriched.count", enrichedCount },
            { "game.count", games.Count },
        });

        var (rateLimitedProvider, retryAfterSeconds) = LongestRateLimit(rateLimitBackoffs);
        return new EnrichmentBatchResult(
            enrichedCount,
            rawgEnrichedTitles,
            openCriticEnrichedTitles,
            rateLimitedProvider,
            retryAfterSeconds,
            resumeFromIndex is { } from ? games.Skip(from).Select(g => g.GameId).ToList() : [],
            rejectedProviders,
            enrichmentService.TransportUnavailableProviders.Order().ToList(),
            StoppedReason(rateLimitedProvider, timeBudgetExhausted));
    }

    private static string? GenreId(IReadOnlyDictionary<string, string> genreIdsByName, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : genreIdsByName.GetValueOrDefault(name.ToLowerInvariant());

    private static string? StoppedReason(EnrichmentProvider? rateLimitedProvider, bool timeBudgetExhausted)
    {
        if (rateLimitedProvider is not null)
        {
            return JobStoppedReasons.RateLimited;
        }

        return timeBudgetExhausted ? JobStoppedReasons.TimeBudget : null;
    }

    private static (EnrichmentProvider? Provider, double? RetryAfterSeconds) LongestRateLimit(
        IReadOnlyDictionary<EnrichmentProvider, double> backoffs)
    {
        if (backoffs.Count == 0)
        {
            return (null, null);
        }

        var best = backoffs.Aggregate((a, b) => b.Value > a.Value ? b : a);
        return (best.Key, best.Value);
    }
}
