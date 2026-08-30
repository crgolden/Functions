namespace Functions.Curator.Enrichment;

using OpenCritic;
using Psn;
using Rawg;

public sealed record EnrichmentCredentials
{
    public RawgCredential? Rawg { get; init; }

    public OpenCriticCredential? OpenCritic { get; init; }

    public PsnSessionRotation? Psn { get; init; }
}
