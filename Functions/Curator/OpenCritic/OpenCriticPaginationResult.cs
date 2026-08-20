namespace Functions.Curator.OpenCritic;

public sealed record OpenCriticPaginationResult(
    IReadOnlyList<OpenCriticGame> Games,
    int NextSkip,
    bool Exhausted);
