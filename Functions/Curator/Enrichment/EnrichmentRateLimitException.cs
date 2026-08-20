namespace Functions.Curator.Enrichment;

using System.Globalization;

public sealed class EnrichmentRateLimitException : Exception
{
    public EnrichmentRateLimitException(EnrichmentProvider provider, double retryAfterSeconds)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"{provider.ToWireName()} rate limit hit; retry after {retryAfterSeconds:F0}s"))
    {
        Provider = provider;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public EnrichmentProvider Provider { get; }

    public double RetryAfterSeconds { get; }
}
