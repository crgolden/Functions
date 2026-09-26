namespace Functions.Curator.Library;

using System.Text.Json;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;

public sealed class LibraryRefreshContinuationProcessor
{
    private readonly LibraryRepository _libraryRepository;
    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly EnrichmentBatchProcessor _batchProcessor;
    private readonly RejectedProviderRecorder _rejectedProviderRecorder;
    private readonly JobRunsRepository _jobRuns;
    private readonly ContinuationScheduler _continuationScheduler;

    public LibraryRefreshContinuationProcessor(
        LibraryRepository libraryRepository,
        EnrichmentRepository enrichmentRepository,
        EnrichmentBatchProcessor batchProcessor,
        RejectedProviderRecorder rejectedProviderRecorder,
        JobRunsRepository jobRuns,
        ContinuationScheduler continuationScheduler)
    {
        _libraryRepository = libraryRepository;
        _enrichmentRepository = enrichmentRepository;
        _batchProcessor = batchProcessor;
        _rejectedProviderRecorder = rejectedProviderRecorder;
        _jobRuns = jobRuns;
        _continuationScheduler = continuationScheduler;
    }

    public async Task<object> RunAsync(
        Guid runId,
        Guid identitySub,
        IReadOnlyList<Guid> remainingGameIds,
        EnrichmentContext enrichment,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        var publisherTierRules = await _enrichmentRepository
            .ListPublisherTierRulesAsync(cancellationToken)
            .ConfigureAwait(false);
        var continuationGames = await _libraryRepository
            .GetGamesForContinuationAsync(identitySub, remainingGameIds, cancellationToken)
            .ConfigureAwait(false);
        var gamesById = continuationGames.ToDictionary(game => game.GameId, EqualityComparer<Guid>.Default);

        var candidates = remainingGameIds
            .Where(gamesById.ContainsKey)
            .Select(gameId => gamesById[gameId])
            .Select(game => new EnrichmentCandidate(game.GameId, game.Title, game.ProductId, game.TitleId, game.NativePs5))
            .ToList();

        var enrichResult = await _batchProcessor
            .EnrichGamesAsync(
                enrichment,
                candidates,
                publisherTierRules,
                timeBudget: timeBudget,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (enrichResult.RejectedProviders.Count > 0)
        {
            await _rejectedProviderRecorder
                .RecordAsync(identitySub, enrichResult.RejectedProviders, cancellationToken)
                .ConfigureAwait(false);
        }

        var existingRun = await _jobRuns.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        var existing = ParseExistingSummary(existingRun?.ResultSummary);

        var mergedRawgTitles = MergeOrderPreservingDeduped(existing.RawgEnrichedTitles, enrichResult.RawgEnrichedTitles);
        var mergedOpenCriticTitles = MergeOrderPreservingDeduped(
            existing.OpenCriticEnrichedTitles, enrichResult.OpenCriticEnrichedTitles);
        var opencriticTopupIncomplete = existing.OpenCriticTopupIncomplete || enrichment.Service.OpencriticTopupIncomplete;
        var mergedRejectedProviders = MergeSorted(existing.RejectedProviders, enrichResult.RejectedProviders.ToWireNames());
        var mergedUnavailableProviders = MergeSorted(
            existing.UnavailableProviders, enrichResult.UnavailableProviders.ToWireNames());

        if (enrichResult.StoppedReason is { } stoppedReason)
        {
            var continuationSummary = new LibraryRefreshContinuationSummary
            {
                RawgEnrichedTitles = mergedRawgTitles,
                OpenCriticEnrichedTitles = mergedOpenCriticTitles,
                OpenCriticTopupIncomplete = opencriticTopupIncomplete,
                StoppedReason = stoppedReason,
                RateLimitedProvider = enrichResult.RateLimitedProvider?.ToWireName(),
                RetryAfterSeconds = enrichResult.RetryAfterSeconds ?? 0,
                RemainingCount = enrichResult.RemainingGameIds.Count,
                RejectedProviders = mergedRejectedProviders,
                UnavailableProviders = mergedUnavailableProviders,
            };

            throw await _continuationScheduler
                .ScheduleAsync(
                    runId,
                    identitySub,
                    continuationSummary,
                    enrichResult.RemainingGameIds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new LibraryRefreshResultSummary
        {
            RawgEnrichedTitles = mergedRawgTitles,
            OpenCriticEnrichedTitles = mergedOpenCriticTitles,
            OpenCriticTopupIncomplete = opencriticTopupIncomplete,
            RejectedProviders = mergedRejectedProviders,
            UnavailableProviders = mergedUnavailableProviders,
        };
    }

    private static LibraryRefreshResultSummary ParseExistingSummary(string? resultSummaryJson)
    {
        if (resultSummaryJson is null)
        {
            return new LibraryRefreshResultSummary();
        }

        try
        {
            return JsonSerializer.Deserialize<LibraryRefreshResultSummary>(resultSummaryJson)
                ?? new LibraryRefreshResultSummary();
        }
        catch (JsonException)
        {
            return new LibraryRefreshResultSummary();
        }
    }

    private static List<string> MergeOrderPreservingDeduped(IReadOnlyList<string> existing, IReadOnlyList<string> incoming)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return existing.Concat(incoming).Where(title => seen.Add(title)).ToList();
    }

    private static List<string> MergeSorted(IReadOnlyList<string> existing, IReadOnlyList<string> incoming) =>
        existing.Concat(incoming).Distinct(StringComparer.Ordinal).OrderBy(provider => provider, StringComparer.Ordinal).ToList();
}
