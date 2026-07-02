namespace Functions;

using System.Diagnostics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

// Single place that observes every function invocation's failures — HTTP, Service Bus, and timer
// triggers alike — instead of each worker recording (or not recording, or silently swallowing)
// exceptions on its own. Always rethrows: this only adds telemetry, it never changes whether a
// Service Bus message gets completed, abandoned, or retried.
internal sealed class ExceptionHandlingMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var activity = Activity.Current;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            Telemetry.Metrics.ExceptionOccurred(ex.GetType().Name, context.FunctionDefinition.Name);
            throw;
        }
    }
}
