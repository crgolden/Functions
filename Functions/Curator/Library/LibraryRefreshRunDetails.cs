namespace Functions.Curator.Library;

using Functions.Curator.Jobs;

public static class LibraryRefreshRunDetails
{
    public static string Paused(Guid runId) => $"{runId} paused";

    public static string StoodDown(Guid runId) => $"{runId} stood down";

    public static Func<Exception, string?> PlannedStop(Guid runId) =>
        exception => exception switch
        {
            ContinuationScheduledException => Paused(runId),
            JobRunStoodDownException => StoodDown(runId),
            _ => null,
        };
}
