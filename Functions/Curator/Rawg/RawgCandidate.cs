namespace Functions.Curator.Rawg;

public sealed record RawgCandidate(
    int RawgGameId,
    string Name,
    IReadOnlySet<int> PlatformIds,
    string? Released = null,
    double? Metacritic = null,
    RawgNamed? EsrbRating = null);
