namespace Functions.Curator.Library;

using Jobs;

public static class ContinuationScheduler
{
    public static async Task<Exception> ScheduleAsync(
        Guid runId,
        Guid identitySub,
        LibraryRefreshContinuationSummary summary,
        IReadOnlyList<Guid> remainingGameIds,
        JobRunsRepository jobRuns,
        LibraryRefreshQueuePublisher continuationPublisher,
        CancellationToken cancellationToken = default)
    {
        var newSeq = await jobRuns.TryMarkRateLimitedAsync(runId, summary, cancellationToken).ConfigureAwait(false);
        if (newSeq is null)
        {
            return JobRunStoodDownException.ForRun(runId);
        }

        await continuationPublisher
            .PublishContinuationAsync(
                runId,
                identitySub,
                remainingGameIds,
                summary.RateLimitedProvider,
                summary.RetryAfterSeconds,
                newSeq.Value,
                cancellationToken)
            .ConfigureAwait(false);

        return summary.RateLimitedProvider is { } provider
            ? ContinuationScheduledException.RateLimited(provider, summary.RetryAfterSeconds)
            : ContinuationScheduledException.TimeBudgetExhausted(remainingGameIds.Count);
    }
}
