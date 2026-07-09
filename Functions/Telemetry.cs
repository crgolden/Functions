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

        // The Meter itself keeps every instrument it creates alive for its lifetime, so the observable
        // gauges below don't need to be held in fields — only registered once, here.
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

        /// <summary>Records an unhandled exception caught by <see cref="ExceptionHandlingMiddleware"/>.</summary>
        /// <param name="exceptionType">The short type name of the exception.</param>
        /// <param name="functionName">The name of the function whose invocation threw.</param>
        public static void ExceptionOccurred(string exceptionType, string functionName) =>
            ExceptionCounter.Add(1, new TagList { { "exception.type", exceptionType }, { "function.name", functionName } });

        /// <summary>Records a Census geocode attempt falling back to (0,0).</summary>
        /// <param name="reason">Why the fallback occurred: http-error, exception, or no-match.</param>
        public static void GeocoderFallback(string reason) =>
            GeocoderFallbackCounter.Add(1, new TagList { { "reason", reason } });

        /// <summary>Records the outcome of a zip-backfill attempt.</summary>
        /// <param name="result">"success" or "failure".</param>
        public static void ZipBackfillAttempted(string result) =>
            ZipBackfillCounter.Add(1, new TagList { { "result", result } });

        /// <summary>Records the latest active/dead-lettered message counts for a Service Bus queue.</summary>
        /// <param name="queue">The queue name.</param>
        /// <param name="activeMessageCount">The queue's current active message count.</param>
        /// <param name="deadLetterMessageCount">The queue's current dead-lettered message count.</param>
        public static void RecordQueueDepth(string queue, long activeMessageCount, long deadLetterMessageCount)
        {
            QueueActiveCounts[queue] = activeMessageCount;
            QueueDeadLetterCounts[queue] = deadLetterMessageCount;
        }
    }

    internal static class Tracing
    {
        // ExceptionHandlingMiddleware already records unexpected failures onto the current Activity
        // (SetStatus/AddException), which rides the OTel pipeline into Tempo independent of Serilog's
        // level filter. This is the same mechanism for failures a worker has already handled (not
        // rethrown) — ILogger would be a silent no-op here: Serilog only ships in production, and
        // host.json's logLevel filtering makes the outcome level-dependent even then.
        /// <summary>Records an expected, already-handled failure as a trace event instead of an exception.</summary>
        /// <param name="reason">A short, dot-namespaced event name, e.g. "scrape.expected-failure".</param>
        /// <param name="detail">Free-form detail attached as the event's "detail" tag.</param>
        public static void RecordHandledFailure(string reason, string detail) =>
            Activity.Current?.AddEvent(new ActivityEvent(reason, tags: new ActivityTagsCollection { { "detail", detail } }));
    }
}
