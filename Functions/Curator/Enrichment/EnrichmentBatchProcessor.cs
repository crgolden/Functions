namespace Functions.Curator.Enrichment;

using System.Diagnostics;
using Functions.Curator.Jobs;

public sealed class EnrichmentBatchProcessor
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
    private const string GameCountTag = "game.count";
    private const string EnrichedCountTag = "enriched.count";
    private const string ElapsedMinutesTag = "elapsed.minutes";

    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly Telemetry _telemetry;

    public EnrichmentBatchProcessor(EnrichmentRepository enrichmentRepository, Telemetry telemetry)
    {
        _enrichmentRepository = enrichmentRepository;
        _telemetry = telemetry;
    }

    public async Task<EnrichmentBatchResult> EnrichGamesAsync(
        EnrichmentContext enrichment,
        IReadOnlyList<EnrichmentCandidate> games,
        IReadOnlyList<PublisherTierRule> publisherTierRules,
        bool stopOnFirstProviderFailure = false,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        var enrichmentService = enrichment.Service;
        var tierRules = PublisherTierRuleSet.Prepare(publisherTierRules);
        Telemetry.Tracing.RecordEvent(BatchStartedEvent, new ActivityTagsCollection { { GameCountTag, games.Count } });
        var genreRows = await _enrichmentRepository.GetActiveGenresAsync(cancellationToken);
        var (genrePriorities, genreIdsByName) = IndexGenres(genreRows);

        var enrichedCount = 0;
        var rawgEnrichedTitles = new List<string>();
        var openCriticEnrichedTitles = new List<string>();
        var psnEnrichedTitles = new List<string>();
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
                    { EnrichedCountTag, enrichedCount },
                    { GameCountTag, games.Count },
                    { ElapsedMinutesTag, timeBudget.Elapsed.TotalMinutes },
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
                    enrichment.Credentials,
                    cancellationToken,
                    candidate.Providers);
            }
            catch (EnrichmentRateLimitException exc)
            {
                _telemetry.ProviderDisabled(exc.Provider.ToWireName(), RateLimitedReason);
                Telemetry.Tracing.RecordHandledException(RateLimitedEvent, exc);
                resumeFromIndex ??= index;
                if (StopsOnRateLimit(rateLimitBackoffs, exc, stopOnFirstProviderFailure))
                {
                    break;
                }

                enrichmentService.DisableProvider(exc.Provider);
                continue;
            }
            catch (EnrichmentAuthException exc)
            {
                _telemetry.ProviderDisabled(exc.Provider.ToWireName(), KeyRejectedReason);
                Telemetry.Tracing.RecordHandledException(KeyRejectedEvent, exc);
                if (StopsOnKeyRejection(rejectedProviders, exc, stopOnFirstProviderFailure))
                {
                    resumeFromIndex ??= index;
                    break;
                }

                enrichmentService.DisableProvider(exc.Provider);
                continue;
            }

            var genreId = GenreId(genreIdsByName, result.Genre);
            var subgenreId = GenreId(genreIdsByName, result.Subgenre);
            await _enrichmentRepository.SaveGameEnrichmentAsync(
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
                    RawgEnriched: result.RawgEnriched,
                    OpencriticEnriched: result.OpencriticEnriched,
                    RawgAttempted: result.RawgAttempted,
                    PsnEnriched: result.PsnEnriched,
                    OpencriticAttempted: result.OpencriticAttempted,
                    PsnAttempted: result.PsnAttempted),
                cancellationToken);
            enrichedCount++;
            RecordEnrichedTitles(result, candidate.Title, rawgEnrichedTitles, openCriticEnrichedTitles, psnEnrichedTitles);
            index++;
            _telemetry.GamesEnriched(1);
            ReportProgress(enrichedCount, games.Count);
        }

        Telemetry.Tracing.RecordEvent(BatchFinishedEvent, new ActivityTagsCollection
        {
            { EnrichedCountTag, enrichedCount },
            { GameCountTag, games.Count },
        });

        var (rateLimitedProvider, retryAfterSeconds) = LongestRateLimit(rateLimitBackoffs);
        return new EnrichmentBatchResult(
            enrichedCount,
            rawgEnrichedTitles,
            openCriticEnrichedTitles,
            psnEnrichedTitles,
            rateLimitedProvider,
            retryAfterSeconds,
            RemainingGameIds(games, resumeFromIndex),
            rejectedProviders,
            enrichmentService.TransportUnavailableProviders.Order().ToList(),
            StoppedReason(rateLimitedProvider, timeBudgetExhausted));
    }

    private static (Dictionary<string, int> Priorities, Dictionary<string, Guid> IdsByName) IndexGenres(
        List<ActiveGenre> genreRows)
    {
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
        var idsByName = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in genreRows)
        {
            var lower = row.Name.ToLowerInvariant();
            priorities[lower] = row.Priority;
            idsByName[lower] = row.GenreId;
        }

        return (priorities, idsByName);
    }

    private static bool StopsOnRateLimit(
        Dictionary<EnrichmentProvider, double> rateLimitBackoffs,
        EnrichmentRateLimitException exception,
        bool stopOnFirstProviderFailure)
    {
        var alreadyRateLimited = !rateLimitBackoffs.TryAdd(exception.Provider, exception.RetryAfterSeconds);
        return stopOnFirstProviderFailure || alreadyRateLimited;
    }

    private static bool StopsOnKeyRejection(
        List<EnrichmentProvider> rejectedProviders,
        EnrichmentAuthException exception,
        bool stopOnFirstProviderFailure)
    {
        var alreadyRejected = rejectedProviders.Contains(exception.Provider);
        if (!alreadyRejected)
        {
            rejectedProviders.Add(exception.Provider);
        }

        return stopOnFirstProviderFailure || alreadyRejected;
    }

    private static void RecordEnrichedTitles(
        EnrichmentResult result,
        string title,
        List<string> rawgEnrichedTitles,
        List<string> openCriticEnrichedTitles,
        List<string> psnEnrichedTitles)
    {
        if (result.RawgEnriched)
        {
            rawgEnrichedTitles.Add(title);
        }

        if (result.OpencriticEnriched)
        {
            openCriticEnrichedTitles.Add(title);
        }

        if (result.PsnEnriched)
        {
            psnEnrichedTitles.Add(title);
        }
    }

    private static void ReportProgress(int enrichedCount, int gameCount)
    {
        if (enrichedCount % ProgressReportInterval != 0)
        {
            return;
        }

        Telemetry.Tracing.RecordEvent(ProgressEvent, new ActivityTagsCollection
        {
            { EnrichedCountTag, enrichedCount },
            { GameCountTag, gameCount },
        });
    }

    private static List<Guid> RemainingGameIds(IReadOnlyList<EnrichmentCandidate> games, int? resumeFromIndex) =>
        resumeFromIndex is { } from ? games.Skip(from).Select(g => g.GameId).ToList() : [];

    private static Guid? GenreId(IReadOnlyDictionary<string, Guid> genreIdsByName, string? name) =>
        !string.IsNullOrWhiteSpace(name) && genreIdsByName.TryGetValue(name.ToLowerInvariant(), out var genreId) ? genreId : null;

    private static string? StoppedReason(EnrichmentProvider? rateLimitedProvider, bool timeBudgetExhausted)
    {
        if (rateLimitedProvider is not null)
        {
            return JobStoppedReasons.RateLimited;
        }

        return timeBudgetExhausted ? JobStoppedReasons.TimeBudget : null;
    }

    private static (EnrichmentProvider? Provider, double? RetryAfterSeconds) LongestRateLimit(
        Dictionary<EnrichmentProvider, double> backoffs)
    {
        if (backoffs.Count == 0)
        {
            return (null, null);
        }

        var best = backoffs.Aggregate((a, b) => b.Value > a.Value ? b : a);
        return (best.Key, best.Value);
    }
}
