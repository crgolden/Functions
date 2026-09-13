namespace Functions.Tests.Unit;

using Curator.Enrichment;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class GenreReconciliationServiceTests
{
    [Fact]
    public void ReconcileGenres_WhenPsnHasTags_UsesPsnTagsInsteadOfRawg()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
        var psnTag = TestValues.NewGenre();
        var rawgTag = TestValues.NewGenre();

        // Act
        var (genre, _) = GenreReconciliationService.ReconcileGenres([psnTag], [rawgTag], priorities);

        // Assert
        Assert.Equal(psnTag, genre);
    }

    [Fact]
    public void ReconcileGenres_WhenPsnHasNoTags_FallsBackToRawg()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
        var rawgTag = TestValues.NewGenre();

        // Act
        var (genre, _) = GenreReconciliationService.ReconcileGenres([], [rawgTag], priorities);

        // Assert
        Assert.Equal(rawgTag, genre);
    }

    [Fact]
    public void ReconcileGenres_WhenNeitherHasTags_ReturnsNullGenreAndSubgenre()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);

        // Act
        var (genre, subgenre) = GenreReconciliationService.ReconcileGenres([], [], priorities);

        // Assert
        Assert.Null(genre);
        Assert.Null(subgenre);
    }
}
