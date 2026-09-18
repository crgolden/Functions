namespace Functions.Curator.Enrichment;

using JetBrains.Annotations;

[PublicAPI]
public sealed record EnrichmentCandidate(
    Guid GameId,
    string Title,
    string? ProductId,
    string? TitleId,
    bool IsPs5,
    EnrichmentNeed? Needed = null)
{
    public EnrichmentNeed Providers => Needed ?? EnrichmentNeed.EveryProvider(GameId);
}
