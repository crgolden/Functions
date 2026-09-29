namespace Functions.Curator.Enrichment;

using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;

public sealed record EnrichmentCredentials
{
    public IReadOnlyList<RawgCredential> Rawg { get; init; } = [];

    public IReadOnlyList<OpenCriticCredential> OpenCritic { get; init; } = [];

    public required PsnSessionRotation Psn { get; init; }

    public bool HasRawg => Rawg.Count > 0;

    public bool HasOpenCritic => OpenCritic.Count > 0;
}
