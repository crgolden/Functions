namespace Functions.Curator.Library;

using System.Text.Json;
using Enrichment;
using Jobs;

public static class LibraryRefreshContinuationProcessor
{
    public static async Task<object> RunAsync(
        string runId,
        string identitySub,
        IReadOnlyList<string> remainingGameIds,
        LibraryRepository libraryRepository,
        EnrichmentOrchestrationService enrichmentService,
        EnrichmentRepository enrichmentRepository,
        EnrichmentKeysRepository enrichmentKeysRepository,
        AccountActionLogRepository auditRepository,
        JobRunsRepository jobRuns,
        LibraryRefreshQueuePublisher continuationPublisher,
        IReadOnlyList<PublisherTierRule> publisherTierRules,
        EnrichmentCredentials credentials,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        var continuationGames = await libraryRepository
            .GetGamesForContinuationAsync(identitySub, remainingGameIds, cancellationToken)
            .ConfigureAwait(false);
        var gamesById = continuationGames.ToDictionary(game => game.GameId, StringComparer.Ordinal);

        var candidates = remainingGameIds
            .Where(gamesById.ContainsKey)
            .Select(gameId => gamesById[gameId])
            .Select(game => new EnrichmentCandidate(game.GameId, game.Title, game.ProductId, game.TitleId, game.NativePs5))
            .ToList();

        var enrichResult = await EnrichmentBatchProcessor
            .EnrichGamesAsync(
                enrichmentService,
                enrichmentRepository,
                candidates,
                publisherTierRules,
                credentials,
                timeBudget: timeBudget,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (enrichResult.RejectedProviders.Count > 0)
        {
            await RejectedProviderRecorder
                .RecordAsync(
                    identitySub,
                    enrichResult.RejectedProviders,
                    enrichmentKeysRepository,
                    auditRepository,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var existingRun = await jobRuns.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        var existing = ParseExistingSummary(existingRun?.ResultSummary);

        var mergedRawgTitles = MergeOrderPreservingDeduped(existing.RawgEnrichedTitles, enrichResult.RawgEnrichedTitles);
        var mergedOpenCriticTitles = MergeOrderPreservingDeduped(
            existing.OpenCriticEnrichedTitles, enrichResult.OpenCriticEnrichedTitles);
        var opencriticTopupIncomplete = existing.OpenCriticTopupIncomplete || enrichmentService.OpencriticTopupIncomplete;
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

            throw await ContinuationScheduler
                .ScheduleAsync(
                    runId,
                    identitySub,
                    continuationSummary,
                    enrichResult.RemainingGameIds,
                    jobRuns,
                    continuationPublisher,
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
