namespace Functions.Curator.Jobs;

public sealed class ContinuationScheduledException : Exception
{
    private ContinuationScheduledException(
        string message,
        string stoppedReason,
        string? provider,
        double retryAfterSeconds)
        : base(message)
    {
        StoppedReason = stoppedReason;
        Provider = provider;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public string StoppedReason { get; }

    public string? Provider { get; }

    public double RetryAfterSeconds { get; }

    public static ContinuationScheduledException RateLimited(string provider, double retryAfterSeconds) =>
        new(
            $"Rate limited by {provider}; retry scheduled in {retryAfterSeconds:F0}s.",
            JobStoppedReasons.RateLimited,
            provider,
            retryAfterSeconds);

    public static ContinuationScheduledException TimeBudgetExhausted(int remainingCount) =>
        new(
            $"Time budget exhausted with {remainingCount} game(s) left; continuation scheduled immediately.",
            JobStoppedReasons.TimeBudget,
            provider: null,
            retryAfterSeconds: 0);
}
