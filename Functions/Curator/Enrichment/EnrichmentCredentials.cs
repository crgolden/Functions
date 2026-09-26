namespace Functions.Curator.Enrichment;

using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;

public sealed record EnrichmentCredentials
{
    public RawgCredential? Rawg { get; init; }

    public OpenCriticCredential? OpenCritic { get; init; }

    public PsnSessionRotation? Psn { get; init; }
}
