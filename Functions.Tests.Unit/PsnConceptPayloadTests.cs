namespace Functions.Tests.Unit;

using System.Text.Json;
using Curator.Psn;

[Trait("Category", "Unit")]
public sealed class PsnConceptPayloadTests
{
    [Fact]
    public void Id_ReadsTheNumberPsnSends()
    {
        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>("""{"id": 201930}""");

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(201930, concept.Id);
    }

    [Fact]
    public void Id_ReadsANumericString_BecauseTheEntitlementsEndpointSendsTheSameValueAsText()
    {
        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>("""{"id": "201930"}""");

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(201930, concept.Id);
    }

    [Fact]
    public void StarRatingScore_ReadsTheDecimalStringPsnSends()
    {
        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(
            """{"id": 201930, "starRating": {"total": "923549", "score": "4.47"}}""");

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(4.47, concept.StarRating?.Score);
    }

    [Fact]
    public void ReleaseDate_HasNoDate_WhenPsnPublishesOnlyAComingSoonLabel()
    {
        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(
            """{"id": 10000264, "releaseDate": {"localizedDate": "Prochainement", "type": "COMING_SOON"}}""");

        // Assert
        Assert.NotNull(concept);
        Assert.Equal("COMING_SOON", concept.ReleaseDate?.Type);
        Assert.Null(concept.ReleaseDate?.Date);
    }

    [Fact]
    public void CompatibilityNoticeValue_KeepsTheJsonKindPerNoticeType()
    {
        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>("""
            {"id": 201930, "compatibilityNotices": [
                {"type": "NO_OF_NETWORK_PLAYERS", "value": 30},
                {"type": "REMOTE_PLAY_SUPPORTED", "value": true},
                {"type": "PS5_VIBRATION", "value": "OPTIONAL", "targetPlatforms": ["PS5"]}
            ]}
            """);

        // Assert
        Assert.NotNull(concept);
        Assert.Equal(
            [JsonValueKind.Number, JsonValueKind.True, JsonValueKind.String],
            concept.CompatibilityNotices.Select(notice => notice.Value.ValueKind));
    }

    [Fact]
    public void Genres_TitleIds_AndNotices_AreEmpty_WhenPsnOmitsThoseKeys()
    {
        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>("""{"id": 201930}""");

        // Assert
        Assert.NotNull(concept);
        Assert.Empty(concept.Genres);
        Assert.Empty(concept.TitleIds);
        Assert.Empty(concept.CompatibilityNotices);
    }

    [Fact]
    public void Images_AreEmpty_WhenPsnSendsMediaWithoutThem()
    {
        // Act
        var concept = JsonSerializer.Deserialize<PsnConceptPayload>(
            """{"id": 201930, "media": {"videos": []}}""");

        // Assert
        Assert.NotNull(concept);
        Assert.Empty(concept.Media?.Images ?? []);
    }
}
