namespace Functions.Tests.Unit;

using System.Collections.ObjectModel;
using Curator.Enrichment;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class GenreServiceTests
{
    [Fact]
    public void PickGenreSubgenre_WithNoTags_ReturnsNullGenreAndSubgenre()
    {
        // Arrange
        var priorities = ReadOnlyDictionary<string, int>.Empty;

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
        var mostSpecificGenre = TestValues.NewGenre();
        var lessSpecificGenre = TestValues.NewGenre();
        var unrankedGenre = TestValues.NewGenre();
        var mostSpecificRank = Random.Shared.Next(0, 5);
        var lessSpecificRank = mostSpecificRank + Random.Shared.Next(1, 10);
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [mostSpecificGenre.ToLowerInvariant()] = mostSpecificRank,
            [lessSpecificGenre.ToLowerInvariant()] = lessSpecificRank,
        };

        // Act
        var (genre, subgenre) = GenreService.PickGenreSubgenre(
            [lessSpecificGenre, unrankedGenre, mostSpecificGenre], priorities);

        // Assert
        Assert.Equal(mostSpecificGenre, genre);
        Assert.Equal(lessSpecificGenre, subgenre);
    }

    [Fact]
    public void PickGenreSubgenre_TagsAbsentFromPriorities_KeepTheirOriginalOrderBelowEveryListedTag()
    {
        // Arrange
        var rankedGenre = TestValues.NewGenre();
        var firstUnrankedGenre = TestValues.NewGenre();
        var secondUnrankedGenre = TestValues.NewGenre();
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [rankedGenre.ToLowerInvariant()] = Random.Shared.Next(0, 10),
        };

        // Act
        var (genre, subgenre) = GenreService.PickGenreSubgenre(
            [firstUnrankedGenre, rankedGenre, secondUnrankedGenre], priorities);

        // Assert
        Assert.Equal(rankedGenre, genre);
        Assert.Equal(firstUnrankedGenre, subgenre);
    }

    [Fact]
    public void PickGenreSubgenre_WithOneTag_LeavesSubgenreNull()
    {
        // Arrange
        var onlyGenre = TestValues.NewGenre();
        var priorities = ReadOnlyDictionary<string, int>.Empty;

        // Act
        var (genre, subgenre) = GenreService.PickGenreSubgenre([onlyGenre], priorities);

        // Assert
        Assert.Equal(onlyGenre, genre);
        Assert.Null(subgenre);
    }
}
