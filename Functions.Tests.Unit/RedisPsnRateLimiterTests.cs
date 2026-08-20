namespace Functions.Tests.Unit;

using Curator.Psn;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;

[Trait("Category", "Unit")]
public sealed class RedisPsnRateLimiterTests
{
    private const int MaxRequests = 3;
    private const double WindowSeconds = 60;

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly RedisKey Key = RedisPsnRateLimiter.DefaultKey;

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public void DefaultKey_IsSharedByEveryAccount_SoTheBudgetMatchesPlayStationsPerClientQuota()
    {
        Assert.Equal("curator:psn:ratelimit", RedisPsnRateLimiter.DefaultKey);
    }

    [Fact]
    public async Task AcquireAsync_TrimsTheWindowRecordsTheCallAndRefreshesTheTtl()
    {
        // Arrange
        var seconds = Now.ToUnixTimeMilliseconds() / 1000.0;
        StubTrim(seconds - WindowSeconds);
        StubLength(0);
        var score = double.NaN;
        StubAdd(s => score = s);
        StubExpire();

        // Act
        await Limiter().AcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(seconds, score);
        _databaseMock.Verify(
            d => d.KeyExpireAsync(Key, TimeSpan.FromSeconds(WindowSeconds + 60), ExpireWhen.Always, CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_DoesNotInspectTheOldestCall_WhenTheWindowHasRoom()
    {
        // Arrange
        StubTrim((Now.ToUnixTimeMilliseconds() / 1000.0) - WindowSeconds);
        StubLength(MaxRequests - 1);
        StubAdd();
        StubExpire();

        // Act
        await Limiter().AcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        _databaseMock.Verify(
            d => d.SortedSetRangeByRankWithScoresAsync(
                It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<Order>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [Fact]
    public async Task AcquireAsync_WaitsUntilTheOldestCallLeavesTheWindow_WhenTheBudgetIsSpent()
    {
        // Arrange
        var seconds = Now.ToUnixTimeMilliseconds() / 1000.0;
        StubTrim(seconds - WindowSeconds);
        StubLength(MaxRequests);
        StubOldest(seconds - (WindowSeconds - 10));
        StubAdd();
        StubExpire();

        // Act
        var acquire = Limiter().AcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(acquire.IsCompleted);
        _timeProvider.Advance(TimeSpan.FromSeconds(10));
        await acquire;
    }

    [Fact]
    public async Task AcquireAsync_DoesNotWait_WhenTheOldestCallHasAlreadyLeftTheWindow()
    {
        // Arrange
        var seconds = Now.ToUnixTimeMilliseconds() / 1000.0;
        StubTrim(seconds - WindowSeconds);
        StubLength(MaxRequests);
        StubOldest(seconds - WindowSeconds);
        StubAdd();
        StubExpire();

        // Act
        var acquire = Limiter().AcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(acquire.IsCompleted);
        await acquire;
    }

    [Fact]
    public async Task AcquireAsync_DoesNotWait_WhenTheWindowIsFullButHoldsNoEntries()
    {
        // Arrange
        StubTrim((Now.ToUnixTimeMilliseconds() / 1000.0) - WindowSeconds);
        StubLength(MaxRequests);
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankWithScoresAsync(Key, 0, 0, Order.Ascending, CommandFlags.None))
            .ReturnsAsync([]);
        StubAdd();
        StubExpire();

        // Act
        var acquire = Limiter().AcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(acquire.IsCompleted);
        await acquire;
    }

    private RedisPsnRateLimiter Limiter() =>
        new(_databaseMock.Object, RedisPsnRateLimiter.DefaultKey, MaxRequests, WindowSeconds, _timeProvider);

    private void StubTrim(double windowStart) =>
        _databaseMock
            .Setup(d => d.SortedSetRemoveRangeByScoreAsync(Key, 0, windowStart, Exclude.None, CommandFlags.None))
            .ReturnsAsync(0);

    private void StubLength(long count) =>
        _databaseMock
            .Setup(d => d.SortedSetLengthAsync(
                Key, double.NegativeInfinity, double.PositiveInfinity, Exclude.None, CommandFlags.None))
            .ReturnsAsync(count);

    private void StubOldest(double score) =>
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankWithScoresAsync(Key, 0, 0, Order.Ascending, CommandFlags.None))
            .ReturnsAsync([new SortedSetEntry("oldest", score)]);

    private void StubAdd(Action<double>? onScore = null) =>
        _databaseMock
            .Setup(d => d.SortedSetAddAsync(
                Key, It.IsAny<RedisValue>(), It.IsAny<double>(), SortedSetWhen.Always, CommandFlags.None))
            .Callback<RedisKey, RedisValue, double, SortedSetWhen, CommandFlags>(
                (_, _, score, _, _) => onScore?.Invoke(score))
            .ReturnsAsync(true);

    private void StubExpire() =>
        _databaseMock
            .Setup(d => d.KeyExpireAsync(
                Key, TimeSpan.FromSeconds(WindowSeconds + 60), ExpireWhen.Always, CommandFlags.None))
            .ReturnsAsync(true);
}
