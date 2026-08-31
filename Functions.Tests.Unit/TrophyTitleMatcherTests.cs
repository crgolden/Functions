namespace Functions.Tests.Unit;

using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class TrophyTitleMatcherTests
{
    [Fact]
    public void MatchTitles_MatchesAGameToItsTrophyTitle_WhenTheNamesAgree()
    {
        // Arrange
        var npCommunicationId = NewNpCommunicationId();
        var gameId = NewGameId();
        var titles = new[] { new TrophyTitle(npCommunicationId, "Bloodborne", NewProgress()) };
        var games = new[] { (gameId, "Bloodborne") };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Equal(npCommunicationId, matched[gameId].NpCommunicationId);
    }

    [Fact]
    public void MatchTitles_LeavesAGameUnmatched_WhenNoTitleClearsTheThreshold()
    {
        // Arrange
        var titles = new[] { new TrophyTitle("NPWR001", "Gran Turismo 7", 10) };
        var games = new[] { ("game-1", "Bloodborne") };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_IgnoresATitleWithNoProgress_BecauseItCanReportNoCompletion()
    {
        // Arrange
        var titles = new[] { new TrophyTitle("NPWR001", "Bloodborne", null) };
        var games = new[] { ("game-1", "Bloodborne") };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_IgnoresATitleWithNoName()
    {
        // Arrange
        var titles = new[] { new TrophyTitle("NPWR001", null, 50) };
        var games = new[] { ("game-1", "Bloodborne") };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_ClaimsATrophyTitleForOneGameOnly_SoANearDuplicateCannotAlsoTakeIt()
    {
        // Arrange
        var remasterGameId = NewGameId();
        var exactTitleGameId = NewGameId();
        var titles = new[] { new TrophyTitle(NewNpCommunicationId(), "The Last of Us Part II", NewProgress()) };
        var games = new[]
        {
            (remasterGameId, "The Last of Us Part II Remastered"),
            (exactTitleGameId, "The Last of Us Part II"),
        };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        var claim = Assert.Single(matched);
        Assert.Equal(exactTitleGameId, claim.Key);
    }

    [Fact]
    public void MatchTitles_ClaimsOneTitlePerGame_SoAGameCannotTakeTwo()
    {
        // Arrange
        var firstOfferedTitleId = NewNpCommunicationId();
        var gameId = NewGameId();
        var titles = new[]
        {
            new TrophyTitle(firstOfferedTitleId, "Bloodborne", NewProgress()),
            new TrophyTitle(NewNpCommunicationId(), "Bloodborne", NewProgress()),
        };
        var games = new[] { (gameId, "Bloodborne") };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Single(matched);
        Assert.Equal(firstOfferedTitleId, matched[gameId].NpCommunicationId);
    }

    [Fact]
    public void MatchTitles_ReturnsNothing_WhenThereAreNoGames()
    {
        // Arrange
        var titles = new[] { new TrophyTitle("NPWR001", "Bloodborne", 73) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, []);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_ReturnsNothing_WhenTheUserHasNoTrophyTitles()
    {
        // Arrange
        var games = new[] { ("game-1", "Bloodborne") };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles([], games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_HonoursAThresholdRaisedAboveTheDefault()
    {
        // Arrange
        var titles = new[] { new TrophyTitle(NewNpCommunicationId(), "Bloodborne Remastered", NewProgress()) };
        var games = new[] { (NewGameId(), "Bloodborne") };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games, threshold: 0.99);

        // Assert
        Assert.Empty(matched);
    }

    private static string NewNpCommunicationId() => TestValues.NewNpCommunicationId();

    private static string NewGameId() => $"game-{Guid.NewGuid():N}";

    private static int NewProgress() => Random.Shared.Next(1, 100);
}
