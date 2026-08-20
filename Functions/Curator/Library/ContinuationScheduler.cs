namespace Functions.Curator.Library;

using Jobs;

public static class ContinuationScheduler
{
    public static async Task<ContinuationScheduledException> ScheduleAsync(
        string runId,
        string identitySub,
        LibraryRefreshContinuationSummary summary,
        IReadOnlyList<string> remainingGameIds,
        JobRunsRepository jobRuns,
        LibraryRefreshQueuePublisher continuationPublisher,
        CancellationToken cancellationToken = default)
    {
        var newSeq = await jobRuns.MarkRateLimitedAsync(runId, summary, cancellationToken).ConfigureAwait(false);
        await continuationPublisher
            .PublishContinuationAsync(
                runId,
                identitySub,
                remainingGameIds,
                summary.RateLimitedProvider,
                summary.RetryAfterSeconds,
                newSeq,
                cancellationToken)
            .ConfigureAwait(false);

        return summary.RateLimitedProvider is { } provider
            ? ContinuationScheduledException.RateLimited(provider, summary.RetryAfterSeconds)
            : ContinuationScheduledException.TimeBudgetExhausted(remainingGameIds.Count);
    }
}
