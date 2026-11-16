namespace Functions.Curator.Enrichment;

public sealed record PsnCatalogCacheEntry(
    string TitleId,
    string? ConceptId,
    IReadOnlyList<string> Genres,
    double? StarRating,
    string? Publisher,
    DateOnly? ReleaseDate,
    Uri? CoverImageUrl,
    string? ContentRating = null,
    string? RatingAuthority = null,
    bool? Multiplayer = null,
    DateTimeOffset? ConceptFetchedAt = null,
    string? ConceptType = null);
