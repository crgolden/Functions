namespace Functions.Curator.OpenCritic;

public sealed record OpenCriticRefreshOutcome(
    int GamesFetched,
    int ProcessedPlatformCount,
    IReadOnlyList<string> ContendedPlatforms)
{
    public bool EveryPlatformContended => ProcessedPlatformCount == 0 && ContendedPlatforms.Count > 0;
}
