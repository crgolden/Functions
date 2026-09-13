namespace Functions.Tests.Unit;

using Curator.Rawg;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class RedisRawgRateLimiterTests
{
    private const int MaxRequests = 3;
    private const double WindowSeconds = 60;

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly string KeyText = RedisRawgRateLimiter.KeyForUser(TestValues.NewIdentitySub());
    private static readonly RedisKey Key = KeyText;

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public void KeyForUser_ScopesTheBudgetToOneUsersOwnKey_AndTheAdminKeyIsSeparate()
    {
        var identitySub = TestValues.NewIdentitySub();

        Assert.Equal($"curator:rawg:{identitySub}", RedisRawgRateLimiter.KeyForUser(identitySub));
        Assert.Equal("curator:rawg:admin", RedisRawgRateLimiter.AdminKey);
    }

    [Fact]
    public void TheDefaultBudget_IsRawgsMonthlyFreePlanQuotaOverARollingMonth()
    {
        Assert.Equal(20_000, RedisRawgRateLimiter.MonthlyRequestQuota);
        Assert.Equal(RedisRawgRateLimiter.QuotaWindowSeconds, TimeSpan.FromDays(30).TotalSeconds);
    }

    [Fact]
    public async Task TryAcquireAsync_TrimsTheWindowRecordsTheCallAndRefreshesTheTtl_WhenThereIsRoom()
    {
        // Arrange
        var seconds = Now.ToUnixTimeMilliseconds() / 1000.0;
        StubTrim(seconds - WindowSeconds);
        StubLength(MaxRequests - 1);
        var score = double.NaN;
        StubAdd(s => score = s);
        StubExpire();

        // Act
        var wait = await Limiter().TryAcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(wait);
        Assert.Equal(seconds, score);
        _databaseMock.Verify(
            d => d.KeyExpireAsync(Key, TimeSpan.FromSeconds(WindowSeconds + RedisRawgRateLimiter.TtlMarginSeconds), ExpireWhen.Always, CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_ReportsTheWaitUntilTheOldestCallLeavesTheWindow_AndRecordsNothing_WhenTheBudgetIsSpent()
    {
        // Arrange
        var seconds = Now.ToUnixTimeMilliseconds() / 1000.0;
        var secondsUntilTheOldestCallExpires = Random.Shared.Next(1, (int)WindowSeconds);
        StubTrim(seconds - WindowSeconds);
        StubLength(MaxRequests);
        StubOldest(seconds - (WindowSeconds - secondsUntilTheOldestCallExpires));

        // Act
        var wait = await Limiter().TryAcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(secondsUntilTheOldestCallExpires, wait);
        _databaseMock.Verify(
            d => d.SortedSetAddAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [Fact]
    public async Task TryAcquireAsync_Acquires_WhenTheWindowIsFullButItsOldestCallHasAlreadyExpired()
    {
        // Arrange
        var seconds = Now.ToUnixTimeMilliseconds() / 1000.0;
        StubTrim(seconds - WindowSeconds);
        StubLength(MaxRequests);
        StubOldest(seconds - WindowSeconds);
        StubAdd();
        StubExpire();

        // Act
        var wait = await Limiter().TryAcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(wait);
    }

    private RedisRawgRateLimiter Limiter() =>
        new(_databaseMock.Object, KeyText, MaxRequests, WindowSeconds, _timeProvider);

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
                Key, TimeSpan.FromSeconds(WindowSeconds + RedisRawgRateLimiter.TtlMarginSeconds), ExpireWhen.Always, CommandFlags.None))
            .ReturnsAsync(true);
}
