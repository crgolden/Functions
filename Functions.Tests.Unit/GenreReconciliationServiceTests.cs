namespace Functions.Tests.Unit;

using Curator.Enrichment;

[Trait("Category", "Unit")]
public sealed class GenreReconciliationServiceTests
{
    [Fact]
    public void ReconcileGenres_WhenPsnHasTags_UsesPsnTagsInsteadOfRawg()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);

        // Act
        var (genre, _) = GenreReconciliationService.ReconcileGenres(["Action"], ["Simulation"], priorities);

        // Assert
        Assert.Equal("Action", genre);
    }

    [Fact]
    public void ReconcileGenres_WhenPsnHasNoTags_FallsBackToRawg()
    {
        // Arrange
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);

        // Act
        var (genre, _) = GenreReconciliationService.ReconcileGenres([], ["Simulation"], priorities);

        // Assert
        Assert.Equal("Simulation", genre);
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
