namespace Functions.Tests.Unit;

using Functions.Curator.Jobs;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Extensions.Time.Testing;
using static Shared.Testing.Generated;

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
        // Arrange
        var budget = new JobTimeBudget(TimeSpan.Zero);

        // Act
        var expired = budget.Expired;

        // Assert
        Assert.True(expired);
    }

    [Fact]
    public void Default_LeavesHeadroomUnderTheHostFunctionTimeout()
    {
        // Act
        var defaultBudget = JobTimeBudget.Default;

        // Assert
        Assert.True(HostJson.FunctionTimeout > defaultBudget);
    }
}
