namespace Functions.Curator.Enrichment;

public sealed class RateLimitBackoff
{
    public const double DefaultRetrySeconds = 3600.0;
    public const double MaxRetrySeconds = 86400.0;

    private double _nextSeconds;

    public RateLimitBackoff(double nextSeconds = DefaultRetrySeconds) => _nextSeconds = nextSeconds;

    public static double Next(double previousRetryAfterSeconds) =>
        Math.Min(previousRetryAfterSeconds * 2, MaxRetrySeconds);

    public double RetryAfter(double? hintedSeconds)
    {
        if (hintedSeconds is { } hinted)
        {
            return Math.Min(hinted, MaxRetrySeconds);
        }

        var current = _nextSeconds;
        _nextSeconds = Next(current);
        return current;
    }
}
