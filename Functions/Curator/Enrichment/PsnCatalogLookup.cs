namespace Functions.Curator.Enrichment;

public sealed record PsnCatalogLookup(
    IReadOnlyList<string> Genres,
    double? StarRating,
    string? Publisher = null,
    DateOnly? ReleaseDate = null,
    string? ContentRating = null,
    string? RatingAuthority = null,
    bool? Multiplayer = null);
