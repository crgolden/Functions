namespace Functions.Tests.Unit;

using System.Globalization;
using System.Text.Json;
using Functions.Curator.Psn;
using static Functions.Tests.Unit.PsnConceptPayloadFixtureConstants;

[Trait("Category", "Unit")]
public sealed class PsnConceptPayloadTests
{
    [Fact]
    public void Id_ReadsTheNumberPsnSends()
    {
        // Arrange
        long conceptId = Generated.NewConceptNumericId();
        var body = JsonSerializer.Serialize(new { id = conceptId });

        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(body);

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(conceptId, concept.Id);
    }

    [Fact]
    public void Id_ReadsANumericString_BecauseTheEntitlementsEndpointSendsTheSameValueAsText()
    {
        // Arrange
        long conceptId = Generated.NewConceptNumericId();
        var body = JsonSerializer.Serialize(new { id = conceptId.ToString(CultureInfo.InvariantCulture) });

        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(body);

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(conceptId, concept.Id);
    }

    [Fact]
    public void StarRatingScore_ReadsTheDecimalStringPsnSends()
    {
        // Arrange
        var score = Generated.NewStarRating();
        var body = JsonSerializer.Serialize(new
        {
            id = Generated.NewConceptNumericId(),
            starRating = new
            {
                total = Generated.NewPsnRatingCount().ToString(CultureInfo.InvariantCulture),
                score = score.ToString(CultureInfo.InvariantCulture),
            },
        });

        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(body);

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(score, concept.StarRating?.Score);
    }

    [Fact]
    public void ReleaseDate_HasNoDate_WhenPsnPublishesOnlyAComingSoonLabel()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new
        {
            id = Generated.NewConceptNumericId(),
            releaseDate = new { localizedDate = Generated.NewFieldValue(), type = ComingSoonReleaseDateType },
        });

        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(body);

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(ComingSoonReleaseDateType, concept.ReleaseDate?.Type);
        Assert.Null(concept.ReleaseDate?.Date);
    }

    [Fact]
    public void CompatibilityNoticeValue_KeepsTheJsonKindPerNoticeType()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new
        {
            id = Generated.NewConceptNumericId(),
            compatibilityNotices = new object[]
            {
                new { type = Generated.NewCompatibilityNoticeType(), value = Generated.NewMultiplayerPlayerCount() },
                new { type = Generated.NewCompatibilityNoticeType(), value = true },
                new { type = Generated.NewCompatibilityNoticeType(), value = Generated.NewFieldValue() },
            },
        });

        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(body);

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(
            [JsonValueKind.Number, JsonValueKind.True, JsonValueKind.String],
            concept.CompatibilityNotices.Select(notice => notice.Value.ValueKind));
    }

    [Fact]
    public void Genres_TitleIds_AndNotices_AreEmpty_WhenPsnOmitsThoseKeys()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new { id = Generated.NewConceptNumericId() });

        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(body);

        // Assert
        Assert.NotNull(concept);
        Assert.Empty(concept.Genres);
        Assert.Empty(concept.TitleIds);
        Assert.Empty(concept.CompatibilityNotices);
    }

    [Fact]
    public void Images_AreEmpty_WhenPsnSendsMediaWithoutThem()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new
        {
            id = Generated.NewConceptNumericId(),
            media = new { videos = Array.Empty<object>() },
        });

        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(body);

        // Assert
        Assert.NotNull(concept);
        Assert.Empty(Assert.IsType<PsnConceptMedia>(concept.Media).Images);
    }
}
