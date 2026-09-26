namespace Functions.Tests.Unit;

using Functions.Curator.Enrichment;
using Functions.Curator.Psn;
using Functions.Curator.Store;

[Trait("Category", "Unit")]
public sealed class StoreProductSignalsTests
{
    [Fact]
    public void GenreKeys_ResolvesTheStorefrontsDisplayLabelsToTheVocabularyKeys_AndDropsUnknownLabels()
    {
        // Arrange
        var knownGenreId = Guid.NewGuid();
        var known = new StoreGenre(knownGenreId, Generated.NewGenre(), Generated.NewGenreDisplayName(), Generated.NewRulePriority());
        var product = new StoreProductNode
        {
            Genres =
            [
                new StoreLocalizedGenre { Value = known.DisplayName.ToUpperInvariant() },
                new StoreLocalizedGenre { Value = Generated.NewGenreDisplayName() },
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
        var primaryGenreId = Guid.NewGuid();
        var secondaryGenreId = Guid.NewGuid();
        var primaryPriority = Generated.NewRulePriority();
        var secondaryPriority = primaryPriority + Generated.NewPositiveRankGap();
        var primary = new StoreGenre(primaryGenreId, Generated.NewGenre(), Generated.NewGenreDisplayName(), primaryPriority);
        var secondary = new StoreGenre(secondaryGenreId, Generated.NewGenre(), Generated.NewGenreDisplayName(), secondaryPriority);

        // Act
        var (genreId, subgenreId) = StoreProductSignals.PickGenres([secondary.Name, primary.Name], [primary, secondary]);

        // Assert
        Assert.Equal(primaryGenreId, genreId);
        Assert.Equal(secondaryGenreId, subgenreId);
    }

    [Fact]
    public void Build_TakesTheEsrbRatingOnlyFromTheEsrbAuthority_AndMarksTheProductAsEnrichedAndAttempted()
    {
        // Arrange
        var released = Generated.NewReleaseTimestamp();
        var esrbRated = new StoreProductNode
        {
            ReleaseDate = released,
            ContentRating = new StoreContentRating { Authority = StoreProductSignals.EsrbAuthority, Name = Generated.NewContentRating() },
        };
        var otherwiseRated = esrbRated with
        {
            ContentRating = new StoreContentRating { Authority = Generated.NewRatingAuthority(), Name = Generated.NewContentRating() },
        };

        // Act
        var esrb = StoreProductSignals.Build(esrbRated, null);
        var other = StoreProductSignals.Build(otherwiseRated, null);

        // Assert
        Assert.Equal(esrbRated.ContentRating.Name, esrb.Esrb);
        Assert.Null(other.Esrb);
        Assert.Equal(released.Year, esrb.ReleaseYear);
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
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var released = Generated.NewReleaseTimestamp();
        var product = new StoreProductNode
        {
            Type = Generated.NewConceptType(),
            PublisherName = Generated.NewPublisher(),
            ReleaseDate = released,
            Concept = new StoreConcept { Id = Generated.NewConceptId() },
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

    [Fact]
    public void Build_TakesTheReleaseYearFromTheSameUtcDateTheCacheEntryStores_WhenAnOffsetCrossesNewYear()
    {
        // Arrange
        var localNewYear = Generated.NewNewYearsMidnightAheadOfUtc();
        var product = new StoreProductNode { ReleaseDate = localNewYear };

        // Act
        var signals = StoreProductSignals.Build(product, null);
        var entry = StoreProductSignals.CacheEntry(Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), product, null, []);

        // Assert
        Assert.Equal(localNewYear.UtcDateTime.Year, signals.ReleaseYear);
        Assert.Equal(entry.ReleaseDate?.Year, signals.ReleaseYear);
    }
}
