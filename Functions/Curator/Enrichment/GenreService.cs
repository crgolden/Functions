namespace Functions.Curator.Enrichment;

public static class GenreService
{
    public static (string? Genre, string? Subgenre) PickGenreSubgenre(
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, int> priorities)
    {
        if (tags.Count == 0)
        {
            return (null, null);
        }

        var fallbackRank = priorities.Count > 0 ? priorities.Values.Max() + 1 : 0;
        var ranked = tags
            .OrderBy(tag => priorities.GetValueOrDefault(tag.ToLowerInvariant(), fallbackRank))
            .ToList();

        var genre = ranked[0];
        var subgenre = ranked.Count > 1 ? ranked[1] : null;
        return (genre, subgenre);
    }
}
