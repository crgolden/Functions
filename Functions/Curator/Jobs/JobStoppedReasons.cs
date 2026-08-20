namespace Functions.Curator.Jobs;

public static class JobStoppedReasons
{
    public const string RateLimited = "rate_limited";

    public const string TimeBudget = "time_budget";

    public const string AuthError = "auth_error";

    public const string ConcurrentRun = "concurrent_run";
}
