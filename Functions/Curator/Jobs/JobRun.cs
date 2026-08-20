namespace Functions.Curator.Jobs;

public sealed record JobRun(string RunId, string Kind, Guid? IdentitySub, string Status, string? Error, int Seq)
{
    public string? ResultSummary { get; init; }
}
