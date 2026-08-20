namespace Functions.Curator.Jobs;

public static class JobRunStatuses
{
    public const string Queued = "queued";

    public const string Running = "running";

    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    public const string RateLimited = "rate_limited";
}
