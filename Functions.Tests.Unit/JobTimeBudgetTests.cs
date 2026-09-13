namespace Functions.Tests.Unit;

using Curator.Jobs;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using static TestSupport.TestValues;

[Trait("Category", "Unit")]
public sealed class JobTimeBudgetTests
{
    [Fact]
    public void Expired_IsFalse_UntilTheBudgetIsFullySpent()
    {
        // Arrange
        var allowance = NewJobTimeBudgetAllowance();
        var timeProvider = new FakeTimeProvider();
        var budget = new JobTimeBudget(allowance, timeProvider);

        // Act
        timeProvider.Advance(allowance - TimeSpan.FromTicks(1));

        // Assert
        Assert.False(budget.Expired);
    }

    [Fact]
    public void Expired_IsTrue_OnceTheBudgetIsReached()
    {
        // Arrange
        var allowance = NewJobTimeBudgetAllowance();
        var timeProvider = new FakeTimeProvider();
        var budget = new JobTimeBudget(allowance, timeProvider);

        // Act
        timeProvider.Advance(allowance);

        // Assert
        Assert.True(budget.Expired);
        Assert.Equal(allowance, budget.Elapsed);
    }

    [Fact]
    public void Expired_IsTrueImmediately_WhenThereIsNoBudgetLeftToSpend()
    {
        Assert.True(new JobTimeBudget(TimeSpan.Zero).Expired);
    }

    [Fact]
    public void Default_LeavesHeadroomUnderTheHostFunctionTimeout()
    {
        Assert.True(HostJson.FunctionTimeout > JobTimeBudget.Default);
    }
}
