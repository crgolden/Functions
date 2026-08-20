namespace Functions.Curator.Enrichment;

public sealed class EnrichmentAuthException : Exception
{
    public EnrichmentAuthException(EnrichmentProvider provider, string message)
        : base(message) => Provider = provider;

    public EnrichmentProvider Provider { get; }
}
