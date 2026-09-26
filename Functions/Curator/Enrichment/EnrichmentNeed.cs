namespace Functions.Curator.Enrichment;

public sealed record EnrichmentNeed(Guid GameId, bool Rawg, bool OpenCritic, bool Psn)
{
    public bool Any => Rawg || OpenCritic || Psn;

    public static EnrichmentNeed EveryProvider(Guid gameId) => new(gameId, true, true, true);
}
