namespace Functions.Tests.Unit;

using Curator.Rawg;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class RedisRawgRateLimiterTests
{
    private static readonly int MaxRequests = TestValues.NewRequestQuota();
    private static readonly DateTimeOffset Now = TestValues.NewInstantInsideAMonth();
    private static readonly string KeyText = RedisRawgRateLimiter.KeyForUser(TestValues.NewIdentitySub());
    private static readonly RedisKey MonthKey = RedisRawgRateLimiter.KeyForMonth(KeyText, Now);

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public void KeyForUser_ScopesTheBudgetToOneUsersOwnKey_InTheHyphenatedLowercaseFormCuratorsPythonKeysUse()
    {
        // Arrange
        var identitySub = TestValues.NewIdentitySub();

        // Act
        var key = RedisRawgRateLimiter.KeyForUser(identitySub);

        // Assert
        var keyedSub = key[RedisRawgRateLimiter.UserKeyPrefix.Length..];
        Assert.StartsWith(RedisRawgRateLimiter.UserKeyPrefix, key, StringComparison.Ordinal);
        Assert.Matches(PythonUuidTextFixtureConstants.StrUuidPattern, keyedSub);
        Assert.Equal(identitySub, Guid.Parse(keyedSub));
    }

    [Fact]
    public void TheAdminKey_IsNotAnyUsersKey()
    {
        // Arrange
        var identitySub = TestValues.NewIdentitySub();

        // Act
        var userKey = RedisRawgRateLimiter.KeyForUser(identitySub);

        // Assert
        Assert.NotEqual(userKey, RedisRawgRateLimiter.AdminKey);
    }

    [Fact]
    public void TheDefaultBudget_IsRawgsPublishedMonthlyFreePlanQuota()
    {
        // Arrange
        const int rawgsPublishedFreePlanAllowance = 20_000;

        // Act
        var quota = RedisRawgRateLimiter.MonthlyRequestQuota;

        // Assert
        Assert.Equal(rawgsPublishedFreePlanAllowance, quota);
    }

    [Fact]
    public void KeyForMonth_PutsTwoInstantsInTheSameUtcMonthOnOneBudget()
    {
        // Arrange
        var later = Now.AddMinutes(1);

        // Act
        var first = RedisRawgRateLimiter.KeyForMonth(KeyText, Now);
        var second = RedisRawgRateLimiter.KeyForMonth(KeyText, later);

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void KeyForMonth_StartsAFreshBudgetInTheNextCalendarMonth()
    {
        // Arrange
        var nextMonth = RedisRawgRateLimiter.StartOfNextMonth(Now);

        // Act
        var thisMonth = RedisRawgRateLimiter.KeyForMonth(KeyText, Now);
        var following = RedisRawgRateLimiter.KeyForMonth(KeyText, nextMonth);

        // Assert
        Assert.NotEqual(thisMonth, following);
    }

    [Fact]
    public void StartOfNextMonth_RollsIntoJanuaryOfTheFollowingYear_FromDecember()
    {
        // Arrange
        var followingNewYear = new DateTimeOffset(Now.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var instantInDecember = followingNewYear.AddSeconds(-TestValues.NewSecondsInsideDecember());

        // Act
        var reset = RedisRawgRateLimiter.StartOfNextMonth(instantInDecember);

        // Assert
        Assert.Equal(followingNewYear, reset);
    }

    [Fact]
    public async Task TryAcquireAsync_AdmitsAndExpiresTheBudgetAfterTheMonthEnds_OnTheMonthsFirstCall()
    {
        // Arrange
        StubIncrement(1);
        StubExpire();

        // Act
        var wait = await Limiter().TryAcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(wait);
        _databaseMock.Verify(
            d => d.KeyExpireAsync(MonthKey, ExpiryAfterTheMonthEnds(), ExpireWhen.Always, CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_AdmitsWithoutResettingTheExpiry_OnALaterCallInTheSameMonth()
    {
        // Arrange
        StubIncrement(MaxRequests);

        // Act
        var wait = await Limiter().TryAcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(wait);
        _databaseMock.Verify(
            d => d.KeyExpireAsync(
                It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [Fact]
    public async Task TryAcquireAsync_ReportsTheSecondsUntilTheCalendarMonthResets_WhenTheBudgetIsSpent()
    {
        // Arrange
        StubIncrement(MaxRequests + 1);

        // Act
        var wait = await Limiter().TryAcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((RedisRawgRateLimiter.StartOfNextMonth(Now) - Now).TotalSeconds, wait);
    }

    [Fact]
    public async Task TryAcquireAsync_DecidesFromOneAtomicIncrement_RatherThanAReadFollowedByAWrite()
    {
        // Arrange
        StubIncrement(MaxRequests);

        // Act
        await Limiter().TryAcquireAsync(TestContext.Current.CancellationToken);

        // Assert
        const string reason =
            "The admit-or-refuse decision must come from the value INCR itself returns, in one round "
            + "trip. Counting with a separate read and then writing lets two concurrent runs both observe "
            + "a count below the quota and both admit, which is why the sibling OpenAI limiter needs a "
            + "transaction with a length condition to do the same job over its weighted window.";

        _databaseMock.Verify(d => d.StringIncrementAsync(MonthKey, 1, CommandFlags.None), Times.Once);
        Assert.True(_databaseMock.Invocations.Count(i => i.Method.Name == nameof(IDatabase.StringIncrementAsync)) == 1, reason);
    }

    private RedisRawgRateLimiter Limiter() =>
        new(_databaseMock.Object, KeyText, MaxRequests, _timeProvider);

    private static TimeSpan ExpiryAfterTheMonthEnds() =>
        TimeSpan.FromSeconds(
            (RedisRawgRateLimiter.StartOfNextMonth(Now) - Now).TotalSeconds + RedisRawgRateLimiter.TtlMarginSeconds);

    private void StubIncrement(long spent) =>
        _databaseMock
            .Setup(d => d.StringIncrementAsync(MonthKey, 1, CommandFlags.None))
            .ReturnsAsync(spent);

    private void StubExpire() =>
        _databaseMock
            .Setup(d => d.KeyExpireAsync(MonthKey, ExpiryAfterTheMonthEnds(), ExpireWhen.Always, CommandFlags.None))
            .ReturnsAsync(true);
}
