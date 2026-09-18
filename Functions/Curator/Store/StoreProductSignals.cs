namespace Functions.Curator.Store;

using Enrichment;

public static class StoreProductSignals
{
    internal const string EsrbAuthority = "ESRB";

    public static IReadOnlyList<string> GenreKeys(StoreProductNode product, IReadOnlyList<StoreGenre> genres)
    {
        var byDisplayName = genres.ToDictionary(genre => genre.DisplayName, StringComparer.OrdinalIgnoreCase);
        return product.Genres
            .Select(genre => genre.Value)
            .OfType<string>()
            .Select(label => byDisplayName.GetValueOrDefault(label))
            .OfType<StoreGenre>()
            .Select(genre => genre.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static (Guid? GenreId, Guid? SubgenreId) PickGenres(
        IReadOnlyList<string> genreKeys,
        IReadOnlyList<StoreGenre> genres)
    {
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
        var idsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in genres)
        {
            priorities[genre.Name.ToLowerInvariant()] = genre.Priority;
            idsByName[genre.Name] = genre.GenreId;
        }

        var (pickedGenre, pickedSubgenre) = GenreService.PickGenreSubgenre(genreKeys, priorities);
        return (GenreId(idsByName, pickedGenre), GenreId(idsByName, pickedSubgenre));
    }

    public static GameEnrichmentSignals Build(StoreProductNode? product, StoreStarRating? starRating) => new(
        ReleaseYear: ReleaseYear.FromDate(ReleaseDate(product?.ReleaseDate)),
        Developer: null,
        Publisher: product?.PublisherName,
        Esrb: EsrbRating(product?.ContentRating),
        Multiplayer: null,
        CriticalScore: null,
        OcScore: null,
        OcTier: null,
        OcPercentRecommended: null,
        PsnRating: RatedAverage(starRating),
        ScoreSource: null,
        AaaTier: null,
        RawgEnriched: false,
        OpencriticEnriched: false,
        PsnEnriched: product is not null,
        PsnAttempted: true,
        PsnRatingCount: starRating?.TotalRatingsCount);

    public static PsnCatalogCacheEntry CacheEntry(string titleId, StoreProductNode product, StoreStarRating? starRating, IReadOnlyList<string> genreKeys) =>
        new(
            titleId,
            product.Concept?.Id,
            genreKeys,
            RatedAverage(starRating),
            product.PublisherName,
            ReleaseDate(product.ReleaseDate),
            CoverImageUrl: null,
            product.ContentRating?.Name,
            product.ContentRating?.Authority,
            Multiplayer: null,
            ConceptType: product.Type);

    internal static double? RatedAverage(StoreStarRating? starRating) =>
        starRating is { TotalRatingsCount: > 0 } ? starRating.AverageRating : null;

    private static Guid? GenreId(IReadOnlyDictionary<string, Guid> idsByName, string? name) =>
        !string.IsNullOrWhiteSpace(name) && idsByName.TryGetValue(name, out var genreId) ? genreId : null;

    private static string? EsrbRating(StoreContentRating? rating) =>
        rating is not null && string.Equals(rating.Authority, EsrbAuthority, StringComparison.Ordinal) ? rating.Name : null;

    private static DateOnly? ReleaseDate(DateTimeOffset? released) =>
        released is { } timestamp ? DateOnly.FromDateTime(timestamp.UtcDateTime) : null;
}
