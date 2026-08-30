namespace Functions;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

internal static class Telemetry
{
    internal static class Metrics
    {
        private static readonly Meter Meter = new(nameof(Functions), "1.0.0");

        private static readonly ConcurrentDictionary<string, long> QueueActiveCounts = new();

        private static readonly ConcurrentDictionary<string, long> QueueDeadLetterCounts = new();

        private static readonly Counter<long> ExceptionCounter =
            Meter.CreateCounter<long>("functions.exceptions", description: "Number of unhandled exceptions caught by the global exception-handling middleware.");

        private static readonly Counter<long> GeocoderFallbackCounter =
            Meter.CreateCounter<long>("functions.geocoder.fallbacks", description: "Census geocode attempts that fell back to a zero coordinate.");

        private static readonly Counter<long> ZipBackfillCounter =
            Meter.CreateCounter<long>("functions.geocoder.zip_backfill", description: "Attempts to resolve a missing zip from city/state via a reverse lookup.");

        private static readonly Counter<long> BulkImportRowsCounter =
            Meter.CreateCounter<long>("functions.churches.bulk_import.rows", description: "Church records read from a bulk-import blob, split by whether they were published to the geocoding queue or skipped as duplicates.");

        private static readonly Counter<long> ReGeocodedChurchesCounter =
            Meter.CreateCounter<long>("functions.churches.regeocode.churches", description: "Zero-coordinate church candidates a re-geocode pass considered, split by whether they were updated, still missing coordinates, or not persisted.");

        private static readonly Counter<long> EnrichmentGamesCounter =
            Meter.CreateCounter<long>("functions.curator.enrichment.games", description: "Games enriched, incremented as a batch progresses so an hours-long run is visible before it finishes.");

        private static readonly Counter<long> ProviderDisabledCounter =
            Meter.CreateCounter<long>("functions.curator.enrichment.provider_disabled", description: "Enrichment providers dropped mid-batch after rejecting a key or rate-limiting the caller.");

        private static readonly Counter<long> StaleRedeliveryCounter =
            Meter.CreateCounter<long>("functions.curator.jobs.stale_redeliveries", description: "Job messages redelivered for a run that is no longer current.");

        private static readonly Counter<long> TransientRetryCounter =
            Meter.CreateCounter<long>("functions.curator.jobs.transient_retries", description: "Job messages abandoned for redelivery after a transient infrastructure fault, rather than failed.");

        private static readonly Counter<long> ReapedLeaseCounter =
            Meter.CreateCounter<long>("functions.curator.jobs.reaped_leases", description: "Job runs failed by the reaper because their processing lease expired unrenewed.");

        private static readonly Counter<long> OpenCriticSweepCounter =
            Meter.CreateCounter<long>("functions.curator.opencritic.sweep_games", description: "Games fetched into the OpenCritic cache by the nightly sweep.");

        private static readonly Counter<long> PsnSessionRotationCounter =
            Meter.CreateCounter<long>("functions.curator.psn.session_rotations", description: "Rotations to the next configured npsso after a PSN account rejected a request.");

        static Metrics()
        {
            Meter.CreateObservableGauge(
                "functions.servicebus.queue.active",
                () => QueueActiveCounts.Select(kv => new Measurement<long>(kv.Value, new TagList { { "queue", kv.Key } })),
                description: "Active message count per Service Bus queue, refreshed by QueueDepthMonitorJob.");
            Meter.CreateObservableGauge(
                "functions.servicebus.queue.deadletter",
                () => QueueDeadLetterCounts.Select(kv => new Measurement<long>(kv.Value, new TagList { { "queue", kv.Key } })),
                description: "Dead-lettered message count per Service Bus queue, refreshed by QueueDepthMonitorJob.");
        }

        public static void ExceptionOccurred(string exceptionType, string functionName) =>
            ExceptionCounter.Add(1, new TagList { { "exception.type", exceptionType }, { "function.name", functionName } });

        public static void GeocoderFallback(string reason) =>
            GeocoderFallbackCounter.Add(1, new TagList { { "reason", reason } });

        public static void ZipBackfillAttempted(string result) =>
            ZipBackfillCounter.Add(1, new TagList { { "result", result } });

        public static void BulkImportRows(long rows, string result, string source) =>
            BulkImportRowsCounter.Add(rows, new TagList { { "result", result }, { "source", source } });

        public static void ReGeocoded(long churches, string result) =>
            ReGeocodedChurchesCounter.Add(churches, new TagList { { "result", result } });

        public static void RecordQueueDepth(string queue, long activeMessageCount, long deadLetterMessageCount)
        {
            QueueActiveCounts[queue] = activeMessageCount;
            QueueDeadLetterCounts[queue] = deadLetterMessageCount;
        }

        public static void GamesEnriched(long games) => EnrichmentGamesCounter.Add(games);

        public static void ProviderDisabled(string provider, string reason) =>
            ProviderDisabledCounter.Add(1, new TagList { { "provider", provider }, { "reason", reason } });

        public static void StaleRedelivery(string disposition) =>
            StaleRedeliveryCounter.Add(1, new TagList { { "disposition", disposition } });

        public static void TransientRetry(string messageType) =>
            TransientRetryCounter.Add(1, new TagList { { "job.message_type", messageType } });

        public static void LeasesReaped(long runs) => ReapedLeaseCounter.Add(runs);

        public static void OpenCriticSweepFetched(long games) => OpenCriticSweepCounter.Add(games);

        public static void PsnSessionRotated() => PsnSessionRotationCounter.Add(1);
    }

    internal static class Tracing
    {
        public const string JobRunSpanName = "curator.job.run";

        public const string JobOutcomeTagName = "job.outcome";

        public const string RunIdTagName = "run.id";

        public const string RunSeqTagName = "run.seq";

        public const string MessageTypeTagName = "job.message_type";

        public const string ErrorCodeTagName = "job.error_code";

        private static readonly ActivitySource Source = new(nameof(Functions), "1.0.0");

        public static Activity? StartJobRun(string messageType)
        {
            var activity = Source.StartActivity(JobRunSpanName, ActivityKind.Consumer);
            activity?.SetTag(MessageTypeTagName, messageType);
            return activity;
        }

        public static void RecordJobIdentity(Activity? activity, string runId, int seq)
        {
            activity?.SetTag(RunIdTagName, runId);
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