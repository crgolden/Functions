namespace Functions.Curator.Library;

using Functions.Curator.Jobs;

public sealed class ContinuationScheduler
{
    private readonly JobRunsRepository _jobRuns;
    private readonly LibraryRefreshQueuePublisher _continuationPublisher;

    public ContinuationScheduler(JobRunsRepository jobRuns, LibraryRefreshQueuePublisher continuationPublisher)
    {
        _jobRuns = jobRuns;
        _continuationPublisher = continuationPublisher;
    }

    public async Task<Exception> ScheduleAsync(
        Guid runId,
        Guid identitySub,
        LibraryRefreshContinuationSummary summary,
        IReadOnlyList<Guid> remainingGameIds,
        CancellationToken cancellationToken = default)
    {
        var newSeq = await _jobRuns.TryMarkRateLimitedAsync(runId, summary, cancellationToken).ConfigureAwait(false);
        if (newSeq is null)
        {
            return JobRunStoodDownException.ForRun(runId);
        }

        await _continuationPublisher
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
