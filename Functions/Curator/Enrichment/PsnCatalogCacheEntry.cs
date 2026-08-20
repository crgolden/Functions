namespace Functions.Curator.Enrichment;

public sealed record PsnCatalogCacheEntry(
    string TitleId,
    string? ConceptId,
    IReadOnlyList<string> Genres,
    double? StarRating,
    string? Publisher,
    DateOnly? ReleaseDate,
    string? CoverImageUrl,
    string? ContentRating = null,
    string? RatingAuthority = null,
    bool? Multiplayer = null,
    DateTimeOffset? ConceptFetchedAt = null);
