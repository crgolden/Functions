namespace Functions.Tests.Unit;

using Churches.Extraction;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class RedisOpenAIRateLimiterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly RedisKey Key = RedisOpenAIRateLimiter.Key;
    private static readonly double Seconds = Now.ToUnixTimeMilliseconds() / (double)TimeSpan.MillisecondsPerSecond;

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly Mock<ITransaction> _transactionMock = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public void TheKey_IsTheOneEveryChurchesInstanceShares()
    {
        // Act
        var rateLimitKey = RedisOpenAIRateLimiter.Key;

        // Assert
        Assert.Equal("churches:openai:ratelimit", rateLimitKey);
    }

    [Fact]
    public void TheDefaultBudget_IsTheAzureOpenAIDeploymentsPerMinuteRateLimit()
    {
        // Act
        int[] budget = [RedisOpenAIRateLimiter.DeploymentRequestsPerMinute, RedisOpenAIRateLimiter.DeploymentTokensPerMinute];

        // Assert
        Assert.Equal([200, 200_000], budget);
    }

    [Fact]
    public void TheWindow_IsTheMinuteAzureOpenAICountsItsLimitsOver()
    {
        // Act
        var window = RedisOpenAIRateLimiter.WindowSeconds;

        // Assert
        Assert.Equal(60d, window);
    }

    [Fact]
    public void TokensOf_ReadsBackTheEstimateAMemberWasRecordedWith()
    {
        // Arrange
        var estimatedTokens = Random.Shared.Next(1, 100_000);
        var callId = TestValues.NewOpenAICallId();

        // Act
        var tokens = RedisOpenAIRateLimiter.TokensOf(RedisOpenAIRateLimiter.Member(callId, estimatedTokens));

        // Assert
        Assert.Equal(estimatedTokens, tokens);
    }

    [Fact]
    public async Task TryAcquireAsync_RecordsTheCallWithItsEstimateAndRefreshesTheTtl_WhenTheWindowHasRoom()
    {
        // Arrange
        var maxTokens = Random.Shared.Next(1_000, 100_000);
        var recordedTokens = Random.Shared.Next(1, maxTokens / 2);
        var estimatedTokens = maxTokens - recordedTokens;
        var secondsSinceTheRecordedCall = TestValues.NewSecondsAgoInsideAMinuteWindow();
        StubTrim();
        StubEntries(Entry(recordedTokens, Seconds - secondsSinceTheRecordedCall));
        var member = RedisValue.Null;
        var score = double.NaN;
        StubTransaction((m, s) => (member, score) = (m, s), admitted: true);

        // Act
        var wait = await Limiter(TestValues.NewRateLimitMaxRequests(), maxTokens).TryAcquireAsync(estimatedTokens, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(wait);
        Assert.Equal(estimatedTokens, RedisOpenAIRateLimiter.TokensOf(member));
        Assert.Equal(Seconds, score);
        _transactionMock.Verify(t => t.AddCondition(It.IsAny<Condition>()), Times.Once);
        _transactionMock.Verify(
            t => t.KeyExpireAsync(Key, TimeSpan.FromSeconds(RedisOpenAIRateLimiter.WindowSeconds + RedisOpenAIRateLimiter.TtlMarginSeconds), ExpireWhen.Always, CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_WaitsBriefly_WhenAnotherInstanceChangedTheWindowBetweenTheReadAndTheWrite()
    {
        // Arrange
        var estimatedTokens = TestValues.NewSmallTokenEstimate();
        StubTrim();
        StubEntries();
        StubTransaction((_, _) => { }, admitted: false);

        // Act
        var wait = await Limiter(TestValues.NewRateLimitMaxRequests(), RedisOpenAIRateLimiter.DeploymentTokensPerMinute)
            .TryAcquireAsync(estimatedTokens, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(RedisOpenAIRateLimiter.LostRaceWaitSeconds, wait);
    }

    [Fact]
    public async Task TryAcquireAsync_WaitsUntilEnoughTokensLeaveTheWindow_AndRecordsNothing_WhenTheTokenBudgetIsSpent()
    {
        // Arrange
        var maxTokens = Random.Shared.Next(1_000, 100_000);
        var oldestTokens = Random.Shared.Next(1, maxTokens / 2);
        var secondsUntilTheOldestLeaves = Random.Shared.Next(1, 30);
        var secondsUntilTheNewestLeaves = Random.Shared.Next(30, 60);
        StubTrim();
        StubEntries(
            Entry(oldestTokens, Seconds - (RedisOpenAIRateLimiter.WindowSeconds - secondsUntilTheOldestLeaves)),
            Entry(maxTokens - oldestTokens, Seconds - (RedisOpenAIRateLimiter.WindowSeconds - secondsUntilTheNewestLeaves)));

        // Act
        var wait = await Limiter(RedisOpenAIRateLimiter.DeploymentRequestsPerMinute, maxTokens)
            .TryAcquireAsync(oldestTokens + 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(secondsUntilTheNewestLeaves, wait);
        VerifyNothingRecorded();
    }

    [Fact]
    public async Task TryAcquireAsync_WaitsForTheOldestCall_WhenTheRequestBudgetIsSpentButTokensRemain()
    {
        // Arrange
        var maxRequests = TestValues.NewRateLimitMaxRequests();
        var tokensPerCall = Random.Shared.Next(1, 100);
        var secondsUntilTheOldestLeaves = Random.Shared.Next(1, 30);
        StubTrim();
        StubEntries(FullWindow(maxRequests, tokensPerCall, Seconds - (RedisOpenAIRateLimiter.WindowSeconds - secondsUntilTheOldestLeaves)));

        // Act
        var wait = await Limiter(maxRequests, RedisOpenAIRateLimiter.DeploymentTokensPerMinute)
            .TryAcquireAsync(tokensPerCall, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(secondsUntilTheOldestLeaves, wait);
        VerifyNothingRecorded();
    }

    [Fact]
    public void TheDefaultSpacing_SpreadsTheDeploymentsRequestsEvenlyAcrossTheMinute()
    {
        // Act
        var spreadAcrossTheMinute = RedisOpenAIRateLimiter.MinSecondsBetweenCalls * RedisOpenAIRateLimiter.DeploymentRequestsPerMinute;

        // Assert
        Assert.Equal(RedisOpenAIRateLimiter.WindowSeconds, spreadAcrossTheMinute);
    }

    [Fact]
    public async Task TryAcquireAsync_WaitsOutTheSpacing_AndRecordsNothing_WhenTheNewestCallIsTooRecentDespiteRoomInTheBudget()
    {
        // Arrange
        var secondsSinceTheNewestCall = TestValues.NewExactlyRepresentableSecondsBelow(RedisOpenAIRateLimiter.MinSecondsBetweenCalls);
        var recordedTokens = TestValues.NewSmallTokenEstimate();
        var estimatedTokens = TestValues.NewSmallTokenEstimate();
        StubTrim();
        StubEntries(Entry(recordedTokens, Seconds - secondsSinceTheNewestCall));
        var limiter = new RedisOpenAIRateLimiter(
            _databaseMock.Object,
            timeProvider: _timeProvider);

        // Act
        var wait = await limiter.TryAcquireAsync(estimatedTokens, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(RedisOpenAIRateLimiter.MinSecondsBetweenCalls - secondsSinceTheNewestCall, wait);
        VerifyNothingRecorded();
    }

    [Fact]
    public async Task TryAcquireAsync_WaitsAWholeWindow_WhenTheEstimateAloneExceedsTheTokenBudget()
    {
        // Arrange
        var maxTokens = Random.Shared.Next(1_000, 100_000);
        StubTrim();
        StubEntries();

        // Act
        var wait = await Limiter(RedisOpenAIRateLimiter.DeploymentRequestsPerMinute, maxTokens)
            .TryAcquireAsync(maxTokens + 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(RedisOpenAIRateLimiter.WindowSeconds, wait);
        VerifyNothingRecorded();
    }

    private static SortedSetEntry Entry(int tokens, double score) =>
        new(RedisOpenAIRateLimiter.Member(TestValues.NewOpenAICallId(), tokens), score);

    private static SortedSetEntry[] FullWindow(int count, int tokensPerCall, double oldestScore) =>
        [.. Enumerable.Range(0, count).Select(index => Entry(tokensPerCall, oldestScore + index))];

    private RedisOpenAIRateLimiter Limiter(int maxRequests, int maxTokens) =>
        new(_databaseMock.Object, maxRequests, maxTokens, minSecondsBetweenCalls: 0, _timeProvider);

    private void StubTrim() =>
        _databaseMock
            .Setup(d => d.SortedSetRemoveRangeByScoreAsync(Key, 0, Seconds - RedisOpenAIRateLimiter.WindowSeconds, Exclude.None, CommandFlags.None))
            .ReturnsAsync(0);

    private void StubEntries(params SortedSetEntry[] entries) =>
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankWithScoresAsync(Key, 0, -1, Order.Ascending, CommandFlags.None))
            .ReturnsAsync(entries);

    private void StubTransaction(Action<RedisValue, double> onAdd, bool admitted)
    {
        _databaseMock.Setup(d => d.CreateTransaction(It.IsAny<object?>())).Returns(_transactionMock.Object);
        _transactionMock
            .Setup(t => t.SortedSetAddAsync(
                Key, It.IsAny<RedisValue>(), It.IsAny<double>(), SortedSetWhen.Always, CommandFlags.None))
            .Callback<RedisKey, RedisValue, double, SortedSetWhen, CommandFlags>(
                (_, member, score, _, _) => onAdd(member, score))
            .ReturnsAsync(true);
        _transactionMock
            .Setup(t => t.KeyExpireAsync(
                Key, TimeSpan.FromSeconds(RedisOpenAIRateLimiter.WindowSeconds + RedisOpenAIRateLimiter.TtlMarginSeconds), ExpireWhen.Always, CommandFlags.None))
            .ReturnsAsync(true);
        _transactionMock.Setup(t => t.ExecuteAsync(CommandFlags.None)).ReturnsAsync(admitted);
    }

    private void VerifyNothingRecorded() =>
        _databaseMock.Verify(d => d.CreateTransaction(It.IsAny<object?>()), Times.Never);
}
