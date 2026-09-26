namespace Functions.Tests.Unit;

using Functions.Curator.Psn;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class TrophyTitleMatcherTests
{
    private static readonly double AThresholdNoEditionSuffixCanClear = Math.BitDecrement(1.0);

    [Fact]
    public void MatchTitles_MatchesAGameToItsTrophyTitle_WhenTheNamesAgree()
    {
        // Arrange
        var npCommunicationId = Generated.NewNpCommunicationId();
        var gameId = NewGameId();
        var sharedTitle = Generated.NewLongTitle();
        var titles = new[] { new TrophyTitle(npCommunicationId, sharedTitle, NewTrophyProgress()) };
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
        var trophyTitleName = Generated.NewTokenFromFirstHalfOfAlphabet(24);
        var gameTitleSharingNoCharactersWithIt = Generated.NewTokenFromSecondHalfOfAlphabet(24);
        var titles = new[]
        {
            new TrophyTitle(Generated.NewNpCommunicationId(), trophyTitleName, NewTrophyProgress()),
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
        var sharedTitle = Generated.NewLongTitle();
        var titles = new[] { new TrophyTitle(Generated.NewNpCommunicationId(), sharedTitle, null) };
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
        var titles = new[] { new TrophyTitle(Generated.NewNpCommunicationId(), null, NewTrophyProgress()) };
        var games = new[] { (NewGameId(), Generated.NewLongTitle()) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_ClaimsATrophyTitleForOneGameOnly_SoANearDuplicateCannotAlsoTakeIt()
    {
        // Arrange
        var trophyTitleName = Generated.NewLongTitle();
        var sameTitleWithAnEditionSuffix = Generated.WithAnEditionSuffix(trophyTitleName);
        var editionGameId = NewGameId();
        var exactTitleGameId = NewGameId();
        var titles = new[]
        {
            new TrophyTitle(Generated.NewNpCommunicationId(), trophyTitleName, NewTrophyProgress()),
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
        var firstOfferedTitleId = Generated.NewNpCommunicationId();
        var gameId = NewGameId();
        var sharedTitle = Generated.NewLongTitle();
        var titles = new[]
        {
            new TrophyTitle(firstOfferedTitleId, sharedTitle, NewTrophyProgress()),
            new TrophyTitle(Generated.NewNpCommunicationId(), sharedTitle, NewTrophyProgress()),
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
            new TrophyTitle(Generated.NewNpCommunicationId(), Generated.NewLongTitle(), NewTrophyProgress()),
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
        var games = new[] { (NewGameId(), Generated.NewLongTitle()) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles([], games);

        // Assert
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchTitles_HonoursAThresholdRaisedAboveTheDefault()
    {
        // Arrange
        var gameTitle = Generated.NewLongTitle();
        var sameTitleWithAnEditionSuffix = Generated.WithAnEditionSuffix(gameTitle);
        var titles = new[]
        {
            new TrophyTitle(Generated.NewNpCommunicationId(), sameTitleWithAnEditionSuffix, NewTrophyProgress()),
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
        var gameTitle = Generated.NewLongTitle();
        var sameTitleWithAnEditionSuffix = Generated.WithAnEditionSuffix(gameTitle);
        var titles = new[]
        {
            new TrophyTitle(Generated.NewNpCommunicationId(), sameTitleWithAnEditionSuffix, NewTrophyProgress()),
        };
        var games = new[] { (gameId, gameTitle) };

        // Act
        var matched = TrophyTitleMatcher.MatchTitles(titles, games);

        // Assert
        Assert.Equal(sameTitleWithAnEditionSuffix, matched[gameId].Name);
    }
}
