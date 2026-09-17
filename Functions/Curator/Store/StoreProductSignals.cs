namespace Functions.Curator.Store;

using System.Globalization;
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

    public static (string? GenreId, string? SubgenreId) PickGenres(
        IReadOnlyList<string> genreKeys,
        IReadOnlyList<StoreGenre> genres)
    {
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
        var idsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in genres)
        {
            priorities[genre.Name.ToLowerInvariant()] = genre.Priority;
            idsByName[genre.Name] = genre.GenreId;
        }

        var (pickedGenre, pickedSubgenre) = GenreService.PickGenreSubgenre(genreKeys, priorities);
        return (GenreId(idsByName, pickedGenre), GenreId(idsByName, pickedSubgenre));
    }

    public static GameEnrichmentSignals Build(StoreProductNode? product, StoreStarRating? starRating) => new(
        ReleaseYear: ReleaseYear.FromText(product?.ReleaseDate),
        Developer: null,
        Publisher: product?.PublisherName,
        Esrb: EsrbRating(product?.ContentRating),
        Multiplayer: null,
        CriticalScore: null,
        OcScore: null,
        OcTier: null,
        OcPercentRecommended: null,
        PsnRating: starRating?.AverageRating,
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
            starRating?.AverageRating,
            product.PublisherName,
            ReleaseDate(product.ReleaseDate),
            CoverImageUrl: null,
            product.ContentRating?.Name,
            product.ContentRating?.Authority,
            Multiplayer: null,
            ConceptType: product.Type);

    private static string? GenreId(IReadOnlyDictionary<string, string> idsByName, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : idsByName.GetValueOrDefault(name);

    private static string? EsrbRating(StoreContentRating? rating) =>
        rating is not null && string.Equals(rating.Authority, EsrbAuthority, StringComparison.Ordinal) ? rating.Name : null;

    private static DateOnly? ReleaseDate(string? released) =>
        DateTimeOffset.TryParse(released, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateOnly.FromDateTime(parsed.UtcDateTime)
            : null;
}
