namespace Functions.Tests.Unit;

using System.Text.Json;
using Curator.OpenCritic;

[Trait("Category", "Unit")]
public sealed class OpenCriticGameEntryTests
{
    private const string DeadCellsQueenAndTheSea = """
        {
          "percentRecommended": -1,
          "numReviews": 4,
          "topCriticScore": 93.33333333333333,
          "tier": "Mighty",
          "name": "Dead Cells: The Queen & the Sea",
          "id": 12550,
          "firstReleaseDate": "2022-01-06T00:00:00.000Z",
          "url": "https://opencritic.com/game/12550/dead-cells-the-queen-the-sea"
        }
        """;

    [Fact]
    public void ToGame_TreatsANegativePercentRecommendedAsNoData_NotAsAPercentage()
    {
        // Arrange
        var entry = JsonSerializer.Deserialize<OpenCriticGameEntry>(DeadCellsQueenAndTheSea);

        // Act
        var game = entry?.ToGame(DeadCellsQueenAndTheSea);

        // Assert
        Assert.NotNull(game);
        Assert.Null(game.PercentRecommended);
        Assert.Equal(93.33333333333333, game.TopCriticScore);
    }

    [Fact]
    public void ToGame_TreatsANegativeTopCriticScoreAsNoData()
    {
        // Arrange
        var entry = new OpenCriticGameEntry { Id = 1, Name = "Unreviewed", TopCriticScore = -1, PercentRecommended = 50 };

        // Act
        var game = entry.ToGame("{}");

        // Assert
        Assert.NotNull(game);
        Assert.Null(game.TopCriticScore);
        Assert.Equal(50, game.PercentRecommended);
    }

    [Fact]
    public void ToGame_KeepsAGenuineZero_BecauseZeroIsAScoreAndMinusOneIsAbsence()
    {
        // Arrange
        var entry = new OpenCriticGameEntry { Id = 2, Name = "Panned", TopCriticScore = 0, PercentRecommended = 0 };

        // Act
        var game = entry.ToGame("{}");

        // Assert
        Assert.NotNull(game);
        Assert.Equal(0, game.TopCriticScore);
        Assert.Equal(0, game.PercentRecommended);
    }

    [Fact]
    public void ToGame_ReturnsNull_WhenTheEntryHasNoUsableIdentity()
    {
        Assert.Null(new OpenCriticGameEntry { Id = null, Name = "No id" }.ToGame("{}"));
        Assert.Null(new OpenCriticGameEntry { Id = 3, Name = null }.ToGame("{}"));
        Assert.Null(new OpenCriticGameEntry { Id = 3, Name = string.Empty }.ToGame("{}"));
    }

    [Fact]
    public void Deserialize_PreservesUnmappedFields_SoNothingIsLostFromTheStoredRawPayload()
    {
        // Act
        var entry = JsonSerializer.Deserialize<OpenCriticGameEntry>(DeadCellsQueenAndTheSea);

        // Assert
        Assert.NotNull(entry);
        Assert.Contains("numReviews", entry.AdditionalFields.Keys);
        Assert.Contains("firstReleaseDate", entry.AdditionalFields.Keys);
    }
}
