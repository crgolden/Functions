namespace Functions.Curator.Enrichment;

public sealed record StoreGenre(Guid GenreId, string Name, string DisplayName, int Priority);
