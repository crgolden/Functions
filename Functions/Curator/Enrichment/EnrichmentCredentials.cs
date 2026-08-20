namespace Functions.Curator.Enrichment;

using OpenCritic;
using Psn;
using Rawg;

public sealed record EnrichmentCredentials
{
    public RawgCredential? Rawg { get; init; }

    public OpenCriticCredential? OpenCritic { get; init; }

    public PsnSessionRotation? Psn { get; init; }

    public EnrichmentCredentials Without(EnrichmentProvider provider) => provider switch
    {
        EnrichmentProvider.Rawg => this with { Rawg = null },
        EnrichmentProvider.OpenCritic => this with { OpenCritic = null },
        EnrichmentProvider.Psn => this with { Psn = null },
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}
