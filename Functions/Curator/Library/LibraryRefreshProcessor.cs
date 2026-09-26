namespace Functions.Curator.Library;

using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.Psn;

public sealed class LibraryRefreshProcessor
{
    private readonly LibraryBuildOrchestrator _orchestrator;
    private readonly EnrichmentRepository _enrichmentRepository;
    private readonly RejectedProviderRecorder _rejectedProviderRecorder;
    private readonly ContinuationScheduler _continuationScheduler;

    public LibraryRefreshProcessor(
        LibraryBuildOrchestrator orchestrator,
        EnrichmentRepository enrichmentRepository,
        RejectedProviderRecorder rejectedProviderRecorder,
        ContinuationScheduler continuationScheduler)
    {
        _orchestrator = orchestrator;
        _enrichmentRepository = enrichmentRepository;
        _rejectedProviderRecorder = rejectedProviderRecorder;
        _continuationScheduler = continuationScheduler;
    }

    public async Task<object> RunAsync(
        Guid runId,
        Guid identitySub,
        PsnSession session,
        PsnSession? trophySession,
        EnrichmentContext enrichment,
        JobTimeBudget? timeBudget = null,
        CancellationToken cancellationToken = default)
    {
        var publisherTierRules = await _enrichmentRepository
            .ListPublisherTierRulesAsync(cancellationToken)
            .ConfigureAwait(false);
        var canonicalGames = await _orchestrator
            .CanonicalizeAsync(identitySub, session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var gameIds = await _orchestrator
            .PersistAndLinkAsync(identitySub, canonicalGames, cancellationToken)
            .ConfigureAwait(false);
        await _orchestrator
            .RecordDownloadSizesAsync(identitySub, session, cancellationToken)
            .ConfigureAwait(false);
        var enrichResult = await _orchestrator
            .EnrichDeltaAsync(enrichment, canonicalGames, gameIds, publisherTierRules, timeBudget, cancellationToken)
            .ConfigureAwait(false);
        await _orchestrator
            .MatchTrophiesAsync(identitySub, canonicalGames, gameIds, trophySession, cancellationToken)
            .ConfigureAwait(false);

        if (enrichResult.RejectedProviders.Count > 0)
        {
            await _rejectedProviderRecorder
                .RecordAsync(identitySub, enrichResult.RejectedProviders, cancellationToken)
                .ConfigureAwait(false);
        }

        if (enrichResult.StoppedReason is { } stoppedReason)
        {
            var continuationSummary = new LibraryRefreshContinuationSummary
            {
                RawgEnrichedTitles = enrichResult.RawgEnrichedTitles,
                OpenCriticEnrichedTitles = enrichResult.OpenCriticEnrichedTitles,
                OpenCriticTopupIncomplete = enrichment.Service.OpencriticTopupIncomplete,
                StoppedReason = stoppedReason,
                RateLimitedProvider = enrichResult.RateLimitedProvider?.ToWireName(),
                RetryAfterSeconds = enrichResult.RetryAfterSeconds ?? 0,
                RemainingCount = enrichResult.RemainingGameIds.Count,
                RejectedProviders = enrichResult.RejectedProviders.ToWireNames(),
                UnavailableProviders = enrichResult.UnavailableProviders.ToWireNames(),
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
            RawgEnrichedTitles = enrichResult.RawgEnrichedTitles,
            OpenCriticEnrichedTitles = enrichResult.OpenCriticEnrichedTitles,
            OpenCriticTopupIncomplete = enrichment.Service.OpencriticTopupIncomplete,
            RejectedProviders = enrichResult.RejectedProviders.ToWireNames(),
            UnavailableProviders = enrichResult.UnavailableProviders.ToWireNames(),
        };
    }
}
