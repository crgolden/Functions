namespace Functions.Tests.Unit;

using Curator.Enrichment;
using Curator.Store;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class StoreProductSignalsTests
{
    [Fact]
    public void GenreKeys_ResolvesTheStorefrontsDisplayLabelsToTheVocabularyKeys_AndDropsUnknownLabels()
    {
        // Arrange
        var known = new StoreGenre(Guid.NewGuid().ToString(), TestValues.NewGenre(), TestValues.NewGenreDisplayName(), TestValues.NewRulePriority());
        var product = new StoreProductNode
        {
            Genres =
            [
                new StoreLocalizedGenre { Value = known.DisplayName.ToUpperInvariant() },
                new StoreLocalizedGenre { Value = TestValues.NewGenreDisplayName() },
            ],
        };

        // Act
        var keys = StoreProductSignals.GenreKeys(product, [known]);

        // Assert
        Assert.Equal([known.Name], keys);
    }

    [Fact]
    public void PickGenres_NamesTheGenreIdsThroughTheSharedPriorityPicker()
    {
        // Arrange
        var primary = new StoreGenre(Guid.NewGuid().ToString(), TestValues.NewGenre(), TestValues.NewGenreDisplayName(), 1);
        var secondary = new StoreGenre(Guid.NewGuid().ToString(), TestValues.NewGenre(), TestValues.NewGenreDisplayName(), 2);

        // Act
        var (genreId, subgenreId) = StoreProductSignals.PickGenres([secondary.Name, primary.Name], [primary, secondary]);

        // Assert
        Assert.Equal(primary.GenreId, genreId);
        Assert.Equal(secondary.GenreId, subgenreId);
    }

    [Fact]
    public void Build_TakesTheEsrbRatingOnlyFromTheEsrbAuthority_AndMarksTheProductAsEnrichedAndAttempted()
    {
        // Arrange
        var esrbRated = new StoreProductNode
        {
            ReleaseDate = TestValues.NewReleaseTimestamp().ToString("O"),
            ContentRating = new StoreContentRating { Authority = StoreProductSignals.EsrbAuthority, Name = TestValues.NewContentRating() },
        };
        var otherwiseRated = esrbRated with
        {
            ContentRating = new StoreContentRating { Authority = TestValues.NewRatingAuthority(), Name = TestValues.NewContentRating() },
        };

        // Act
        var esrb = StoreProductSignals.Build(esrbRated, null);
        var other = StoreProductSignals.Build(otherwiseRated, null);

        // Assert
        Assert.Equal(esrbRated.ContentRating.Name, esrb.Esrb);
        Assert.Null(other.Esrb);
        Assert.Equal(ReleaseYear.FromText(esrbRated.ReleaseDate), esrb.ReleaseYear);
        Assert.True(esrb.PsnEnriched);
        Assert.True(esrb.PsnAttempted);
    }

    [Fact]
    public void Build_ForAMissingProduct_RecordsOnlyTheAttempt()
    {
        // Act
        var signals = StoreProductSignals.Build(null, null);

        // Assert
        Assert.False(signals.PsnEnriched);
        Assert.True(signals.PsnAttempted);
        Assert.Null(signals.PsnRating);
        Assert.Null(signals.PsnRatingCount);
    }

    [Fact]
    public void CacheEntry_CarriesTheConceptTypeAndTheReleaseDateAsADate_AndNeverOverwritesArt()
    {
        // Arrange
        var titleId = TestValues.NewTitleId();
        var released = TestValues.NewReleaseTimestamp();
        var product = new StoreProductNode
        {
            Type = TestValues.NewConceptType(),
            PublisherName = TestValues.NewPublisher(),
            ReleaseDate = released.ToString("O"),
            Concept = new StoreConcept { Id = TestValues.NewConceptId() },
        };

        // Act
        var entry = StoreProductSignals.CacheEntry(titleId, product, null, []);

        // Assert
        Assert.Equal(titleId, entry.TitleId);
        Assert.Equal(product.Concept.Id, entry.ConceptId);
        Assert.Equal(product.Type, entry.ConceptType);
        Assert.Equal(DateOnly.FromDateTime(released.UtcDateTime), entry.ReleaseDate);
        Assert.Null(entry.CoverImageUrl);
    }
}
