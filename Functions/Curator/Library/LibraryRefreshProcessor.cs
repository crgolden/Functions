namespace Functions.Curator.Library;

using Enrichment;
using Jobs;
using Psn;

public static class LibraryRefreshProcessor
{
    public static async Task<object> RunAsync(
        string runId,
        string identitySub,
        LibraryBuildOrchestrator orchestrator,
        EnrichmentOrchestrationService enrichmentService,
        EnrichmentKeysRepository enrichmentKeysRepository,
        AccountActionLogRepository auditRepository,
        JobRunsRepository jobRuns,
        LibraryRefreshQueuePublisher continuationPublisher,
        IPsnTrophyClient trophyClient,
        PsnSession session,
        PsnSession? trophySession,
        IReadOnlyList<PublisherTierRule> publisherTierRules,
        EnrichmentCredentials credentials,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        var canonicalGames = await orchestrator
            .CanonicalizeAsync(identitySub, session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var gameIds = await orchestrator
            .PersistAndLinkAsync(identitySub, canonicalGames, cancellationToken)
            .ConfigureAwait(false);
        var enrichResult = await orchestrator
            .EnrichDeltaAsync(
                canonicalGames, gameIds, publisherTierRules, credentials, timeBudget, cancellationToken)
            .ConfigureAwait(false);
        await orchestrator
            .MatchTrophiesAsync(identitySub, canonicalGames, gameIds, trophyClient, trophySession, cancellationToken)
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

        if (enrichResult.StoppedReason is { } stoppedReason)
        {
            var continuationSummary = new LibraryRefreshContinuationSummary
            {
                RawgEnrichedTitles = enrichResult.RawgEnrichedTitles,
                OpenCriticEnrichedTitles = enrichResult.OpenCriticEnrichedTitles,
                OpenCriticTopupIncomplete = enrichmentService.OpencriticTopupIncomplete,
                StoppedReason = stoppedReason,
                RateLimitedProvider = enrichResult.RateLimitedProvider?.ToWireName(),
                RetryAfterSeconds = enrichResult.RetryAfterSeconds ?? 0,
                RemainingCount = enrichResult.RemainingGameIds.Count,
                RejectedProviders = enrichResult.RejectedProviders.ToWireNames(),
                UnavailableProviders = enrichResult.UnavailableProviders.ToWireNames(),
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
            RawgEnrichedTitles = enrichResult.RawgEnrichedTitles,
            OpenCriticEnrichedTitles = enrichResult.OpenCriticEnrichedTitles,
            OpenCriticTopupIncomplete = enrichmentService.OpencriticTopupIncomplete,
            RejectedProviders = enrichResult.RejectedProviders.ToWireNames(),
            UnavailableProviders = enrichResult.UnavailableProviders.ToWireNames(),
        };
    }
}
