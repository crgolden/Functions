namespace Functions.Tests.Unit;

using System.Collections.ObjectModel;
using Functions.Curator.Enrichment;

[Trait("Category", "Unit")]
public sealed class GenreReconciliationServiceTests
{
    [Fact]
    public void ReconcileGenres_WhenPsnHasTags_UsesPsnTagsInsteadOfRawg()
    {
        // Arrange
        var priorities = ReadOnlyDictionary<string, int>.Empty;
        var psnTag = Generated.NewGenre();
        var rawgTag = Generated.NewGenre();

        // Act
        var (genre, _) = GenreReconciliationService.ReconcileGenres([psnTag], [rawgTag], priorities);

        // Assert
        Assert.Equal(psnTag, genre);
    }

    [Fact]
    public void ReconcileGenres_WhenPsnHasNoTags_FallsBackToRawg()
    {
        // Arrange
        var priorities = ReadOnlyDictionary<string, int>.Empty;
        var rawgTag = Generated.NewGenre();

        // Act
        var (genre, _) = GenreReconciliationService.ReconcileGenres([], [rawgTag], priorities);

        // Assert
        Assert.Equal(rawgTag, genre);
    }

    [Fact]
    public void ReconcileGenres_WhenNeitherHasTags_ReturnsNullGenreAndSubgenre()
    {
        // Arrange
        var priorities = ReadOnlyDictionary<string, int>.Empty;

        // Act
        var (genre, subgenre) = GenreReconciliationService.ReconcileGenres([], [], priorities);

        // Assert
        Assert.Null(genre);
        Assert.Null(subgenre);
    }
}
