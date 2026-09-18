namespace Functions.Tests.Unit;

using System.Text.Json;
using Curator.OpenCritic;
using TestSupport;
using static OpenCriticGameEntryFixtureConstants;

[Trait("Category", "Unit")]
public sealed class OpenCriticGameEntryTests
{
    [Fact]
    public void ToGame_TreatsANegativePercentRecommendedAsNoData_NotAsAPercentage()
    {
        // Arrange
        var topCriticScore = TestValues.NewCriticScore();
        var body = UnrecommendedEntryBody(topCriticScore);
        var entry = JsonSerializer.Deserialize<OpenCriticGameEntry>(body);

        // Act
        var game = entry?.ToGame(body);

        // Assert
        Assert.NotNull(game);
        Assert.Null(game.PercentRecommended);
        Assert.Equal(topCriticScore, game.TopCriticScore);
    }

    [Fact]
    public void ToGame_TreatsANegativeTopCriticScoreAsNoData()
    {
        // Arrange
        var percentRecommended = TestValues.NewPercentRecommended();
        var entry = new OpenCriticGameEntry
        {
            Id = TestValues.NewOpenCriticGameId(),
            Name = TestValues.NewGameTitle(),
            TopCriticScore = -1,
            PercentRecommended = percentRecommended,
        };

        // Act
        var game = entry.ToGame(TestValues.NewOpenCriticRawPayload());

        // Assert
        Assert.NotNull(game);
        Assert.Null(game.TopCriticScore);
        Assert.Equal(percentRecommended, game.PercentRecommended);
    }

    [Fact]
    public void ToGame_KeepsAGenuineZero_BecauseZeroIsAScoreAndMinusOneIsAbsence()
    {
        // Arrange
        var entry = new OpenCriticGameEntry
        {
            Id = TestValues.NewOpenCriticGameId(),
            Name = TestValues.NewGameTitle(),
            TopCriticScore = 0,
            PercentRecommended = 0,
        };

        // Act
        var game = entry.ToGame(TestValues.NewOpenCriticRawPayload());

        // Assert
        Assert.NotNull(game);
        Assert.Equal(0, game.TopCriticScore);
        Assert.Equal(0, game.PercentRecommended);
    }

    [Fact]
    public void ToGame_ReturnsNull_WhenTheEntryHasNoUsableIdentity()
    {
        // Arrange
        OpenCriticGameEntry[] entriesWithNoUsableIdentity =
        [
            new() { Id = null, Name = TestValues.NewGameTitle() },
            new() { Id = TestValues.NewOpenCriticGameId(), Name = null },
            new() { Id = TestValues.NewOpenCriticGameId(), Name = string.Empty },
        ];

        // Act
        var games = entriesWithNoUsableIdentity.Select(entry => entry.ToGame(TestValues.NewOpenCriticRawPayload()));

        // Assert
        Assert.Equal([null, null, null], games);
    }

    [Fact]
    public void Deserialize_PreservesUnmappedFields_SoNothingIsLostFromTheStoredRawPayload()
    {
        // Arrange
        var body = UnrecommendedEntryBody(TestValues.NewCriticScore());

        // Act
        var entry = JsonSerializer.Deserialize<OpenCriticGameEntry>(body);

        // Assert
        Assert.NotNull(entry);
        Assert.Contains(ReviewCountPropertyName, entry.AdditionalFields.Keys);
        Assert.Contains(FirstReleaseDatePropertyName, entry.AdditionalFields.Keys);
    }

    private static string UnrecommendedEntryBody(double topCriticScore) => JsonSerializer.Serialize(new
    {
        percentRecommended = -1,
        numReviews = TestValues.NewPsnRatingCount(),
        topCriticScore,
        tier = TestValues.NewOpenCriticTier(),
        name = TestValues.NewGameTitle(),
        id = TestValues.NewOpenCriticGameId(),
        firstReleaseDate = TestValues.NewReleaseTimestamp(),
    });
}
