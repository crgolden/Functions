namespace Functions;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;

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
    }

    internal static class Tracing
    {
        public static void RecordHandledFailure(string reason, string detail) =>
            Activity.Current?.AddEvent(new ActivityEvent(reason, tags: new ActivityTagsCollection { { "detail", detail } }));
    }
}