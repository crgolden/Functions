namespace Functions;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

public sealed class Telemetry : IDisposable
{
    public const string SourceName = nameof(Functions);

    private readonly ActivitySource _activitySource;
    private readonly ConcurrentDictionary<string, long> _queueActiveCounts = new();
    private readonly ConcurrentDictionary<string, long> _queueDeadLetterCounts = new();
    private readonly Counter<long> _exceptionCounter;
    private readonly Counter<long> _geocoderFallbackCounter;
    private readonly Counter<long> _zipBackfillCounter;
    private readonly Counter<long> _bulkImportRowsCounter;
    private readonly Counter<long> _reGeocodedChurchesCounter;
    private readonly Counter<long> _reGeocodedCampusesCounter;
    private readonly Counter<long> _enrichmentGateUnavailableCounter;
    private readonly Counter<long> _enrichmentGamesCounter;
    private readonly Counter<long> _providerDisabledCounter;
    private readonly Counter<long> _staleRedeliveryCounter;
    private readonly Counter<long> _transientRetryCounter;
    private readonly Counter<long> _reapedLeaseCounter;
    private readonly Counter<long> _openCriticSweepCounter;
    private readonly Counter<long> _psnSessionRotationCounter;
    private readonly Counter<long> _storeProductsCounter;

    public Telemetry(IMeterFactory meterFactory, IOptions<TelemetryOptions> telemetryOptions)
    {
        var version = typeof(Telemetry).Assembly.GetName().Version?.ToString();
        var descriptions = telemetryOptions.Value;
        var meter = meterFactory.Create(SourceName, version);
        _activitySource = new ActivitySource(SourceName, version);
        _exceptionCounter = meter.CreateCounter<long>(Metrics.ExceptionsInstrumentName, description: descriptions.ExceptionsDescription);
        _geocoderFallbackCounter = meter.CreateCounter<long>(Metrics.GeocoderFallbacksInstrumentName, description: descriptions.GeocoderFallbacksDescription);
        _zipBackfillCounter = meter.CreateCounter<long>(Metrics.ZipBackfillInstrumentName, description: descriptions.ZipBackfillDescription);
        _bulkImportRowsCounter = meter.CreateCounter<long>(Metrics.BulkImportRowsInstrumentName, description: descriptions.BulkImportRowsDescription);
        _reGeocodedChurchesCounter = meter.CreateCounter<long>(Metrics.ReGeocodedChurchesInstrumentName, description: descriptions.ReGeocodedChurchesDescription);
        _reGeocodedCampusesCounter = meter.CreateCounter<long>(Metrics.ReGeocodedCampusesInstrumentName, description: descriptions.ReGeocodedCampusesDescription);
        _enrichmentGateUnavailableCounter = meter.CreateCounter<long>(Metrics.EnrichmentGateUnavailableInstrumentName, description: descriptions.EnrichmentGateUnavailableDescription);
        _enrichmentGamesCounter = meter.CreateCounter<long>(Metrics.EnrichmentGamesInstrumentName, description: descriptions.EnrichmentGamesDescription);
        _providerDisabledCounter = meter.CreateCounter<long>(Metrics.ProviderDisabledInstrumentName, description: descriptions.ProviderDisabledDescription);
        _staleRedeliveryCounter = meter.CreateCounter<long>(Metrics.StaleRedeliveriesInstrumentName, description: descriptions.StaleRedeliveriesDescription);
        _transientRetryCounter = meter.CreateCounter<long>(Metrics.TransientRetriesInstrumentName, description: descriptions.TransientRetriesDescription);
        _reapedLeaseCounter = meter.CreateCounter<long>(Metrics.ReapedLeasesInstrumentName, description: descriptions.ReapedLeasesDescription);
        _openCriticSweepCounter = meter.CreateCounter<long>(Metrics.OpenCriticSweepGamesInstrumentName, description: descriptions.OpenCriticSweepGamesDescription);
        _psnSessionRotationCounter = meter.CreateCounter<long>(Metrics.PsnSessionRotationsInstrumentName, description: descriptions.PsnSessionRotationsDescription);
        _storeProductsCounter = meter.CreateCounter<long>(Metrics.StoreProductsInstrumentName, description: descriptions.StoreProductsDescription);
        meter.CreateObservableGauge(
            Metrics.QueueActiveInstrumentName,
            () => QueueDepthMeasurements(_queueActiveCounts),
            description: descriptions.QueueActiveDescription);
        meter.CreateObservableGauge(
            Metrics.QueueDeadLetterInstrumentName,
            () => QueueDepthMeasurements(_queueDeadLetterCounts),
            description: descriptions.QueueDeadLetterDescription);
    }

    public Activity? StartJobRun(string messageType)
    {
        var activity = _activitySource.StartActivity(Tracing.JobRunSpanName, ActivityKind.Consumer, parentContext: default);
        activity?.SetTag(Tracing.MessageTypeTagName, messageType);
        return activity;
    }

    public void ExceptionOccurred(string exceptionType, string functionName) =>
        _exceptionCounter.Add(1, new TagList { { Metrics.ExceptionTypeTagName, exceptionType }, { Metrics.FunctionNameTagName, functionName } });

    public void EnrichmentGateUnavailable(string exceptionType) =>
        _enrichmentGateUnavailableCounter.Add(1, new TagList { { Metrics.ExceptionTypeTagName, exceptionType } });

    public void GeocoderFallback(string reason) =>
        _geocoderFallbackCounter.Add(1, new TagList { { Metrics.ReasonTagName, reason } });

    public void ZipBackfillAttempted(string result) =>
        _zipBackfillCounter.Add(1, new TagList { { Metrics.ResultTagName, result } });

    public void BulkImportRows(long rows, string result, string source) =>
        _bulkImportRowsCounter.Add(rows, new TagList { { Metrics.ResultTagName, result }, { Metrics.SourceTagName, source } });

    public void ReGeocoded(long churches, string result) =>
        _reGeocodedChurchesCounter.Add(churches, new TagList { { Metrics.ResultTagName, result } });

    public void ReGeocodedCampuses(long campuses, string result) =>
        _reGeocodedCampusesCounter.Add(campuses, new TagList { { Metrics.ResultTagName, result } });

    public void RecordQueueDepth(string queue, long activeMessageCount, long deadLetterMessageCount)
    {
        _queueActiveCounts[queue] = activeMessageCount;
        _queueDeadLetterCounts[queue] = deadLetterMessageCount;
    }

    public void GamesEnriched(long games) => _enrichmentGamesCounter.Add(games);

    public void ProviderDisabled(string provider, string reason) =>
        _providerDisabledCounter.Add(1, new TagList { { Metrics.ProviderTagName, provider }, { Metrics.ReasonTagName, reason } });

    public void StaleRedelivery(string disposition) =>
        _staleRedeliveryCounter.Add(1, new TagList { { Metrics.DispositionTagName, disposition } });

    public void TransientRetry(string messageType) =>
        _transientRetryCounter.Add(1, new TagList { { Tracing.MessageTypeTagName, messageType } });

    public void LeasesReaped(long runs) => _reapedLeaseCounter.Add(runs);

    public void OpenCriticSweepFetched(long games) => _openCriticSweepCounter.Add(games);

    public void PsnSessionRotated() => _psnSessionRotationCounter.Add(1);

    public void StoreProductsProcessed(long products, string result) =>
        _storeProductsCounter.Add(products, new KeyValuePair<string, object?>(Metrics.ResultTagName, result));

    public void Dispose() => _activitySource.Dispose();

    private static IEnumerable<Measurement<long>> QueueDepthMeasurements(ConcurrentDictionary<string, long> counts) =>
        counts.Select(kv => new Measurement<long>(kv.Value, new TagList { { Metrics.QueueTagName, kv.Key } }));

    internal static class Metrics
    {
        internal const string ExceptionsInstrumentName = "functions.exceptions";

        internal const string GeocoderFallbacksInstrumentName = "functions.geocoder.fallbacks";

        internal const string ZipBackfillInstrumentName = "functions.geocoder.zip_backfill";

        internal const string BulkImportRowsInstrumentName = "functions.churches.bulk_import.rows";

        internal const string ReGeocodedChurchesInstrumentName = "functions.churches.regeocode.churches";

        internal const string ReGeocodedCampusesInstrumentName = "functions.churches.regeocode.campuses";

        internal const string EnrichmentGateUnavailableInstrumentName = "functions.churches.enrichment.gate_unavailable";

        internal const string EnrichmentGamesInstrumentName = "functions.curator.enrichment.games";

        internal const string ProviderDisabledInstrumentName = "functions.curator.enrichment.provider_disabled";

        internal const string StaleRedeliveriesInstrumentName = "functions.curator.jobs.stale_redeliveries";

        internal const string TransientRetriesInstrumentName = "functions.curator.jobs.transient_retries";

        internal const string ReapedLeasesInstrumentName = "functions.curator.jobs.reaped_leases";

        internal const string OpenCriticSweepGamesInstrumentName = "functions.curator.opencritic.sweep_games";

        internal const string PsnSessionRotationsInstrumentName = "functions.curator.psn.session_rotations";

        internal const string StoreProductsInstrumentName = "functions.curator.store.products";

        internal const string QueueActiveInstrumentName = "functions.servicebus.queue.active";

        internal const string QueueDeadLetterInstrumentName = "functions.servicebus.queue.deadletter";

        internal const string ExceptionTypeTagName = "exception.type";

        internal const string FunctionNameTagName = "function.name";

        internal const string ReasonTagName = "reason";

        internal const string ResultTagName = "result";

        internal const string SourceTagName = "source";

        internal const string ProviderTagName = "provider";

        internal const string DispositionTagName = "disposition";

        internal const string QueueTagName = "queue";
    }

    internal static class Tracing
    {
        public const string JobRunSpanName = "curator.job.run";

        public const string JobOutcomeTagName = "job.outcome";

        public const string RunIdTagName = "run.id";

        public const string RunSeqTagName = "run.seq";

        public const string MessageTypeTagName = "job.message_type";

        public const string ErrorCodeTagName = "job.error_code";

        public static void RecordJobIdentity(Activity? activity, Guid runId, int seq)
        {
            activity?.SetTag(RunIdTagName, runId.ToString());
            activity?.SetTag(RunSeqTagName, seq);
        }

        public static void RecordJobOutcome(Activity? activity, string outcome, string? errorCode = null)
        {
            activity?.SetTag(JobOutcomeTagName, outcome);
            if (errorCode is not null)
            {
                activity?.SetTag(ErrorCodeTagName, errorCode);
            }
        }

        public static void RecordJobOutcomeIfAbsent(Activity? activity, string outcome)
        {
            if (activity?.GetTagItem(JobOutcomeTagName) is null)
            {
                RecordJobOutcome(activity, outcome);
            }
        }

        public static void RecordHandledFailure(string reason, string detail) =>
            Activity.Current?.AddEvent(new ActivityEvent(reason, tags: new ActivityTagsCollection { { "detail", detail } }));

        public static void RecordEvent(string name, ActivityTagsCollection tags) =>
            Activity.Current?.AddEvent(new ActivityEvent(name, tags: tags));

        public static void RecordHandledException(
            string name, Exception exception, ActivityTagsCollection? tags = null)
        {
            Activity.Current?.SetStatus(ActivityStatusCode.Error, exception.Message);
            var eventTags = tags ?? new ActivityTagsCollection();
            eventTags["exception.type"] = exception.GetType().FullName;
            eventTags["exception.message"] = exception.Message;
            Activity.Current?.AddEvent(new ActivityEvent(name, tags: eventTags));
        }
    }

    internal static class SemanticConventions
    {
        public const string StabilityOptInVariable = "OTEL_SEMCONV_STABILITY_OPT_IN";

        public const string StableDatabaseConventions = "database";

        public static void OptInToStableDatabaseConventionsUnlessAlreadyChosen()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StabilityOptInVariable)))
            {
                Environment.SetEnvironmentVariable(StabilityOptInVariable, StableDatabaseConventions);
            }
        }
    }
}
