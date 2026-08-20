namespace Functions.Tests.Unit;

using Curator.Jobs;
using Microsoft.Extensions.Time.Testing;

[Trait("Category", "Unit")]
public sealed class JobTimeBudgetTests
{
    [Fact]
    public void Expired_IsFalse_UntilTheBudgetIsFullySpent()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var budget = new JobTimeBudget(TimeSpan.FromMinutes(20), timeProvider);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(19).Add(TimeSpan.FromSeconds(59)));

        // Assert
        Assert.False(budget.Expired);
    }

    [Fact]
    public void Expired_IsTrue_OnceTheBudgetIsReached()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var budget = new JobTimeBudget(TimeSpan.FromMinutes(20), timeProvider);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(20));

        // Assert
        Assert.True(budget.Expired);
        Assert.Equal(TimeSpan.FromMinutes(20), budget.Elapsed);
    }

    [Fact]
    public void Expired_IsTrueImmediately_WhenThereIsNoBudgetLeftToSpend()
    {
        Assert.True(new JobTimeBudget(TimeSpan.Zero).Expired);
    }

    [Fact]
    public void Default_LeavesHeadroomUnderTheHostFunctionTimeout()
    {
        Assert.True(TimeSpan.FromHours(4) > JobTimeBudget.Default);
    }
}
