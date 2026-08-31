namespace Functions.Tests.Unit;

using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class TrophyTitleMatcherTests
{
    private const double AThresholdNoEditionSuffixCanClear = 0.99;

    [Fact]
    public void MatchTitles_MatchesAGameToItsTrophyTitle_WhenTheNamesAgree()
    {
        // Arrange
        var npCommunicationId = TestValues.NewNpCommunicationId();
        var gameId = NewGameId();
        var sharedTitle = TestValues.NewLongTitle();
        var titles = new[] { new TrophyTitle(npCommunicationId, sharedTitle, NewProgress()) };
        var games = new[] { (gameId, sharedTitle) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Equal(npCommunicationId, matched[gameId].NpCommunicationId);
    }

    [Fact]
    public void MatchTitles_LeavesAGameUnmatched_WhenNoTitleClearsTheThreshold()
    {
        // Arrange
        var trophyTitleName = TestValues.NewTokenFromFirstHalfOfAlphabet(24);
        var gameTitleSharingNoCharactersWithIt = TestValues.NewTokenFromSecondHalfOfAlphabet(24);
        var titles = new[]
        {
            new TrophyTitle(TestValues.NewNpCommunicationId(), trophyTitleName, NewProgress()),
        };
        var games = new[] { (NewGameId(), gameTitleSharingNoCharactersWithIt) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_IgnoresATitleWithNoProgress_BecauseItCanReportNoCompletion()
    {
        // Arrange
        var sharedTitle = TestValues.NewLongTitle();
        var titles = new[] { new TrophyTitle(TestValues.NewNpCommunicationId(), sharedTitle, null) };
        var games = new[] { (NewGameId(), sharedTitle) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_IgnoresATitleWithNoName()
    {
        // Arrange
        var titles = new[] { new TrophyTitle(TestValues.NewNpCommunicationId(), null, NewProgress()) };
        var games = new[] { (NewGameId(), TestValues.NewLongTitle()) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_ClaimsATrophyTitleForOneGameOnly_SoANearDuplicateCannotAlsoTakeIt()
    {
        // Arrange
        var trophyTitleName = TestValues.NewLongTitle();
        var sameTitleWithAnEditionSuffix = TestValues.WithAnEditionSuffix(trophyTitleName);
        var editionGameId = NewGameId();
        var exactTitleGameId = NewGameId();
        var titles = new[]
        {
            new TrophyTitle(TestValues.NewNpCommunicationId(), trophyTitleName, NewProgress()),
        };
        var games = new[]
        {
            (editionGameId, sameTitleWithAnEditionSuffix),
            (exactTitleGameId, trophyTitleName),
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
        var firstOfferedTitleId = TestValues.NewNpCommunicationId();
        var gameId = NewGameId();
        var sharedTitle = TestValues.NewLongTitle();
        var titles = new[]
        {
            new TrophyTitle(firstOfferedTitleId, sharedTitle, NewProgress()),
            new TrophyTitle(TestValues.NewNpCommunicationId(), sharedTitle, NewProgress()),
        };
        var games = new[] { (gameId, sharedTitle) };

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
        var titles = new[]
        {
            new TrophyTitle(TestValues.NewNpCommunicationId(), TestValues.NewLongTitle(), NewProgress()),
        };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, []);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_ReturnsNothing_WhenTheUserHasNoTrophyTitles()
    {
        // Arrange
        var games = new[] { (NewGameId(), TestValues.NewLongTitle()) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles([], games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_HonoursAThresholdRaisedAboveTheDefault()
    {
        // Arrange
        var gameTitle = TestValues.NewLongTitle();
        var sameTitleWithAnEditionSuffix = TestValues.WithAnEditionSuffix(gameTitle);
        var titles = new[]
        {
            new TrophyTitle(TestValues.NewNpCommunicationId(), sameTitleWithAnEditionSuffix, NewProgress()),
        };
        var games = new[] { (NewGameId(), gameTitle) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games, threshold: AThresholdNoEditionSuffixCanClear);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_MatchesATitleWithAnEditionSuffix_AtTheDefaultThreshold()
    {
        // Arrange
        var gameId = NewGameId();
        var gameTitle = TestValues.NewLongTitle();
        var sameTitleWithAnEditionSuffix = TestValues.WithAnEditionSuffix(gameTitle);
        var titles = new[]
        {
            new TrophyTitle(TestValues.NewNpCommunicationId(), sameTitleWithAnEditionSuffix, NewProgress()),
        };
        var games = new[] { (gameId, gameTitle) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Equal(sameTitleWithAnEditionSuffix, matched[gameId].Name);
    }

    private static string NewGameId() => $"game-{Guid.NewGuid():N}";

    private static int NewProgress() => Random.Shared.Next(1, 100);
}
