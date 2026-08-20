namespace Functions.Curator.Enrichment;

public static class GenreReconciliationService
{
    public static (string? Genre, string? Subgenre) ReconcileGenres(
        IReadOnlyList<string> psnGenres,
        IReadOnlyList<string> rawgGenres,
        IReadOnlyDictionary<string, int> priorities)
    {
        var tags = psnGenres.Count > 0 ? psnGenres : rawgGenres;
        return GenreService.PickGenreSubgenre(tags, priorities);
    }
}
