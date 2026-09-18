namespace Functions.Curator.Jobs;

using JetBrains.Annotations;

[PublicAPI]
public sealed record JobRun(Guid RunId, string Kind, Guid? IdentitySub, string Status, string? Error, int Seq)
{
    public string? ResultSummary { get; init; }
}
