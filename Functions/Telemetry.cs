namespace Functions;

using System.Diagnostics;
using System.Diagnostics.Metrics;

internal static class Telemetry
{
    internal static class Metrics
    {
        private static readonly Meter Meter = new(nameof(Functions), "1.0.0");

        private static readonly Counter<long> ExceptionCounter =
            Meter.CreateCounter<long>("functions.exceptions", description: "Number of unhandled exceptions caught by the global exception-handling middleware.");

        /// <summary>Records an unhandled exception caught by <see cref="ExceptionHandlingMiddleware"/>.</summary>
        /// <param name="exceptionType">The short type name of the exception.</param>
        /// <param name="functionName">The name of the function whose invocation threw.</param>
        public static void ExceptionOccurred(string exceptionType, string functionName) =>
            ExceptionCounter.Add(1, new TagList { { "exception.type", exceptionType }, { "function.name", functionName } });
    }
}
