namespace Functions.Curator.Enrichment;

public sealed record EnrichmentNeed(string GameId, bool Rawg, bool OpenCritic, bool Psn)
{
    public static EnrichmentNeed EveryProvider(string gameId) => new(gameId, true, true, true);

    public bool Any => Rawg || OpenCritic || Psn;
}
