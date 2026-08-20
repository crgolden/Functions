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

        private static readonly Counter<long> EnrichmentGamesCounter =
            Meter.CreateCounter<long>("functions.curator.enrichment.games", description: "Games enriched, incremented as a batch progresses so an hours-long run is visible before it finishes.");

        private static readonly Counter<long> ProviderDisabledCounter =
            Meter.CreateCounter<long>("functions.curator.enrichment.provider_disabled", description: "Enrichment providers dropped mid-batch after rejecting a key or rate-limiting the caller.");

        private static readonly Counter<long> StaleRedeliveryCounter =
            Meter.CreateCounter<long>("functions.curator.jobs.stale_redeliveries", description: "Job messages redelivered for a run that is no longer current.");

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

        public static void LeasesReaped(long runs) => ReapedLeaseCounter.Add(runs);

        public static void OpenCriticSweepFetched(long games) => OpenCriticSweepCounter.Add(games);

        public static void PsnSessionRotated() => PsnSessionRotationCounter.Add(1);
    }

    internal static class Tracing
    {
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
}