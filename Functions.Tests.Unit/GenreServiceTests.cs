namespace Functions.Tests.Unit;

using Curator.Enrichment;

[Trait("Category", "Unit")]
public sealed class GenreServiceTests
{
    [Fact]
    public void PickGenreSubgenre_WithNoTags_ReturnsNullGenreAndSubgenre()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);

        // Act
        var (genre, subgenre) = GenreService.PickGenreSubgenre([], priorities);

        // Assert
        Assert.Null(genre);
        Assert.Null(subgenre);
    }

    [Fact]
    public void PickGenreSubgenre_RanksTheMostSpecificTagFirst()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["racing"] = 0,
            ["simulation"] = 5,
        };

        // Act
        var (genre, subgenre) = GenreService.PickGenreSubgenre(["Simulation", "Sports", "Racing"], priorities);

        // Assert
        Assert.Equal("Racing", genre);
        Assert.Equal("Simulation", subgenre);
    }

    [Fact]
    public void PickGenreSubgenre_TagsAbsentFromPriorities_KeepTheirOriginalOrderBelowEveryListedTag()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal) { ["rpg"] = 0 };

        // Act
        var (genre, subgenre) = GenreService.PickGenreSubgenre(["Family", "RPG", "Adventure"], priorities);

        // Assert
        Assert.Equal("RPG", genre);
        Assert.Equal("Family", subgenre);
    }

    [Fact]
    public void PickGenreSubgenre_WithOneTag_LeavesSubgenreNull()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);

        // Act
        var (genre, subgenre) = GenreService.PickGenreSubgenre(["Action"], priorities);

        // Assert
        Assert.Equal("Action", genre);
        Assert.Null(subgenre);
    }
}
