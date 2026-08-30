namespace Functions.Curator.Enrichment;

using Psn;

public sealed record PsnCatalogLookup(
    IReadOnlyList<string> Genres,
    double? StarRating,
    string? Publisher = null,
    DateOnly? ReleaseDate = null,
    string? ContentRating = null,
    string? RatingAuthority = null,
    bool? Multiplayer = null,
    bool ConceptResolved = false,
    bool Attempted = false)
{
    public static PsnCatalogLookup NoConcept => new([], null);

    public static PsnCatalogLookup AskedAndFoundNothing => new([], null, Attempted: true);

    public static PsnCatalogLookup ReusedFromCache(PsnCatalogCacheEntry cached) =>
        new(
            cached.Genres,
            cached.StarRating,
            cached.Publisher,
            cached.ReleaseDate,
            cached.ContentRating,
            cached.RatingAuthority,
            cached.Multiplayer);

    public static PsnCatalogLookup FromCache(PsnCatalogCacheEntry cached) =>
        new(
            cached.Genres,
            cached.StarRating,
            cached.Publisher,
            cached.ReleaseDate,
            cached.ContentRating,
            cached.RatingAuthority,
            cached.Multiplayer,
            ConceptResolved: true,
            Attempted: true);

    public static PsnCatalogLookup FromConcept(TitleConcept concept, DateOnly? releaseDate) =>
        new(
            concept.Genres,
            concept.StarRating,
            concept.Publisher,
            releaseDate,
            concept.ContentRating,
            concept.RatingAuthority,
            concept.Multiplayer,
            ConceptResolved: true,
            Attempted: true);
}
