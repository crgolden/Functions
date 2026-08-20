namespace Functions.Tests.Unit;

using Curator.Catalog;

[Trait("Category", "Unit")]
public sealed class FranchiseAssignerTests
{
    [Fact]
    public void AssignFranchise_WithNoMatchingRule_ReturnsNull()
    {
        // Arrange
        var rules = new List<FranchiseRule> { new(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), "halo", "Halo", 1) };

        // Act
        var franchise = FranchiseAssigner.AssignFranchise("Tetris Effect", rules);

        // Assert
        Assert.Null(franchise);
    }

    [Fact]
    public void AssignFranchise_ReturnsTheLowestPriorityMatchingRule()
    {
        // Arrange
        var rules = new List<FranchiseRule>
        {
            new(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), "fantasy", "Generic Fantasy", 20),
            new(Guid.Parse("bef762ad-f421-5413-212e-64479a37a48f"), "final fantasy", "Final Fantasy", 5),
        };

        // Act
        var franchise = FranchiseAssigner.AssignFranchise("Final Fantasy VII Remake", rules);

        // Assert
        Assert.Equal("Final Fantasy", franchise);
    }

    [Fact]
    public void AssignFranchise_LowercasesTheTitleSoLowercaseStoredPatternsMatch()
    {
        // Arrange
        var rules = new List<FranchiseRule> { new(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), "god of war", "God of War", 1) };

        // Act
        var franchise = FranchiseAssigner.AssignFranchise("GOD OF WAR RAGNAROK", rules);

        // Assert
        Assert.Equal("God of War", franchise);
    }

    [Fact]
    public void AssignFranchise_WithTheYearAnchoredAnnoPattern_SkipsTheUnrelatedAnnoMutationem()
    {
        // Arrange
        var rules = new List<FranchiseRule> { new(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), @"\banno \d{4}\b", "Anno", 1) };

        // Act
        var franchise = FranchiseAssigner.AssignFranchise("ANNO: Mutationem", rules);

        // Assert
        Assert.Null(franchise);
    }

    [Fact]
    public void AssignFranchise_WithTheYearAnchoredAnnoPattern_StillMatchesANumberedAnnoTitle()
    {
        // Arrange
        var rules = new List<FranchiseRule> { new(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), @"\banno \d{4}\b", "Anno", 1) };

        // Act
        var franchise = FranchiseAssigner.AssignFranchise("Anno 1800", rules);

        // Assert
        Assert.Equal("Anno", franchise);
    }

    [Fact]
    public void AssignFranchise_WithoutATrailingWordBoundary_MatchesNba2kTitlesWhoseDigitFollowsTheK()
    {
        // Arrange
        var rules = new List<FranchiseRule> { new(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), @"\bnba 2k", "NBA 2K", 1) };

        // Act
        var franchise = FranchiseAssigner.AssignFranchise("NBA 2K16", rules);

        // Assert
        Assert.Equal("NBA 2K", franchise);
    }

    [Fact]
    public void AssignFranchise_WithTheOptionalSeparatorPattern_MatchesWatchDogsWrittenWithAnUnderscore()
    {
        // Arrange
        var rules = new List<FranchiseRule> { new(Guid.Parse("7c2617e2-aad8-6037-47e2-a719b1cc0041"), "watch.?dogs", "Watch Dogs", 1) };

        // Act
        var franchise = FranchiseAssigner.AssignFranchise("Watch_Dogs2", rules);

        // Assert
        Assert.Equal("Watch Dogs", franchise);
    }
}
