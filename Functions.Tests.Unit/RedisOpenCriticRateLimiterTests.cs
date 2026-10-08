namespace Functions.Tests.Unit;

using Functions.Curator.OpenCritic;
using Functions.Tests.Unit.TestSupport;
using Moq;
using StackExchange.Redis;

[Trait("Category", "Unit")]
public sealed class RedisOpenCriticRateLimiterTests
{
    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly Mock<ITransaction> _transactionMock = new(MockBehavior.Strict);
    private readonly TimerSignallingTimeProvider _timeProvider = new();
    private readonly int _requestsPerSecond = Generated.NewRateLimitMaxRequests();

    [Fact]
    public void KeyForCredential_NeverCarriesTheRapidApiKeyItself()
    {
        // Arrange
        var credential = NewCredential();

        // Act
        var key = RedisOpenCriticRateLimiter.KeyForCredential(credential);

        // Assert
        Assert.StartsWith(RedisOpenCriticRateLimiter.KeyPrefix, key, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.RapidApiKey, key, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyForCredential_PutsEveryClientUsingOneRapidApiKeyOnOneBudget()
    {
        // Arrange
        var rapidApiKey = Generated.NewRapidApiKey();

        // Act
        var first = RedisOpenCriticRateLimiter.KeyForCredential(new OpenCriticCredential { RapidApiKey = rapidApiKey });
        var second = RedisOpenCriticRateLimiter.KeyForCredential(new OpenCriticCredential { RapidApiKey = rapidApiKey });

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void KeyForCredential_GivesADifferentRapidApiKeyItsOwnBudget()
    {
        // Act
        var first = RedisOpenCriticRateLimiter.KeyForCredential(NewCredential());
        var second = RedisOpenCriticRateLimiter.KeyForCredential(NewCredential());

        // Assert
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Constructor_RefusesABudgetOfNoRequests()
    {
        // Act
        var exception = Record.Exception(() => new RedisOpenCriticRateLimiter(_databaseMock.Object, 0, _timeProvider));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    [Fact]
    public void LocalSpacing_SpreadsTheConfiguredBudgetEvenlyAcrossOneSecond()
    {
        // Act
        var spacing = Limiter().LocalSpacing;

        // Assert
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / _requestsPerSecond), spacing);
    }

    [Fact]
    public async Task AcquireAsync_IncrementsAndExpiresTheSecondsBudgetInOneTransaction()
    {
        // Arrange
        var credential = NewCredential();
        var secondKey = SecondKey(credential, _timeProvider.GetUtcNow());
        StubTransaction(secondKey, 1);

        // Act
        await Limiter().AcquireAsync(credential, TestContext.Current.CancellationToken);

        // Assert
        _transactionMock.Verify(t => t.StringIncrementAsync(secondKey, 1, CommandFlags.None), Times.Once);
        _transactionMock.Verify(
            t => t.KeyExpireAsync(
                secondKey,
                TimeSpan.FromSeconds(RedisOpenCriticRateLimiter.SecondKeyTtlSeconds),
                ExpireWhen.Always,
                CommandFlags.None),
            Times.Once);
        _transactionMock.Verify(t => t.ExecuteAsync(CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_AdmitsTheLastRequestOfTheSecondsBudget()
    {
        // Arrange
        var credential = NewCredential();
        StubTransaction(SecondKey(credential, _timeProvider.GetUtcNow()), _requestsPerSecond);

        // Act
        var acquire = Limiter().AcquireAsync(credential, TestContext.Current.CancellationToken);

        // Assert
        await acquire;
        Assert.False(_timeProvider.TimerCreated.IsCompleted);
    }

    [Fact]
    public async Task AcquireAsync_WaitsForTheNextSecond_WhenThisSecondsSharedBudgetIsSpent()
    {
        // Arrange
        var credential = NewCredential();
        var now = _timeProvider.GetUtcNow();
        var nextSecond = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() + 1);
        var spentKey = SecondKey(credential, now);
        var nextKey = SecondKey(credential, nextSecond);
        StubTransaction(spentKey, _requestsPerSecond + 1);
        StubTransaction(nextKey, 1);

        // Act
        var acquire = Limiter().AcquireAsync(credential, TestContext.Current.CancellationToken);

        // Assert
        await _timeProvider.TimerCreated;
        Assert.False(acquire.IsCompleted);
        _timeProvider.Advance(nextSecond - now);
        await acquire;
        _transactionMock.Verify(t => t.StringIncrementAsync(nextKey, 1, CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_SpacesTwoRequestsUnderOneRapidApiKey_EvenFromSeparateClients()
    {
        // Arrange
        var credential = NewCredential();
        StubAnyTransaction(1);
        var limiter = Limiter();
        await limiter.AcquireAsync(credential, TestContext.Current.CancellationToken);

        // Act
        var second = limiter.AcquireAsync(
            new OpenCriticCredential { RapidApiKey = credential.RapidApiKey }, TestContext.Current.CancellationToken);

        // Assert
        await _timeProvider.TimerCreated;
        Assert.False(second.IsCompleted);
        _timeProvider.Advance(limiter.LocalSpacing);
        await second;
    }

    [Fact]
    public async Task AcquireAsync_DoesNotSpaceARequestUnderADifferentRapidApiKey()
    {
        // Arrange
        StubAnyTransaction(1);
        var limiter = Limiter();
        await limiter.AcquireAsync(NewCredential(), TestContext.Current.CancellationToken);

        // Act
        var other = limiter.AcquireAsync(NewCredential(), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(other.IsCompletedSuccessfully);
        Assert.False(_timeProvider.TimerCreated.IsCompleted);
    }

    [Fact]
    public async Task AcquireAsync_AdmitsOnTheLocalSpacingAlone_WhenRedisIsUnreachable()
    {
        // Arrange
        StubFailingTransaction(new RedisConnectionException(
            ConnectionFailureType.UnableToConnect,
            CommandFlags.None,
            Generated.NewErrorMessage(),
            null,
            CommandStatus.WaitingToBeSent));

        // Act
        var exception = await Record.ExceptionAsync(
            () => Limiter().AcquireAsync(NewCredential(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task AcquireAsync_LetsAFaultThatIsNotRedisBeingUnreachablePropagate()
    {
        // Arrange
        var fault = new InvalidOperationException(Generated.NewErrorMessage());
        StubFailingTransaction(fault);

        // Act
        var exception = await Record.ExceptionAsync(
            () => Limiter().AcquireAsync(NewCredential(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(fault, exception);
    }

    private static OpenCriticCredential NewCredential() => new() { RapidApiKey = Generated.NewRapidApiKey() };

    private static RedisKey SecondKey(OpenCriticCredential credential, DateTimeOffset instant) =>
        RedisOpenCriticRateLimiter.KeyForSecond(
            RedisOpenCriticRateLimiter.KeyForCredential(credential), instant.ToUnixTimeSeconds());

    private RedisOpenCriticRateLimiter Limiter() => new(_databaseMock.Object, _requestsPerSecond, _timeProvider);

    private void StubTransactionOpening()
    {
        _databaseMock.Setup(d => d.CreateTransaction(It.IsAny<object?>())).Returns(_transactionMock.Object);
        _transactionMock
            .Setup(t => t.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                TimeSpan.FromSeconds(RedisOpenCriticRateLimiter.SecondKeyTtlSeconds),
                ExpireWhen.Always,
                CommandFlags.None))
            .ReturnsAsync(true);
    }

    private void StubTransaction(RedisKey key, long taken)
    {
        StubTransactionOpening();
        _transactionMock.Setup(t => t.StringIncrementAsync(key, 1, CommandFlags.None)).ReturnsAsync(taken);
        _transactionMock.Setup(t => t.ExecuteAsync(CommandFlags.None)).ReturnsAsync(true);
    }

    private void StubAnyTransaction(long taken)
    {
        StubTransactionOpening();
        _transactionMock
            .Setup(t => t.StringIncrementAsync(It.IsAny<RedisKey>(), 1, CommandFlags.None))
            .ReturnsAsync(taken);
        _transactionMock.Setup(t => t.ExecuteAsync(CommandFlags.None)).ReturnsAsync(true);
    }

    private void StubFailingTransaction(Exception fault)
    {
        StubTransactionOpening();
        _transactionMock
            .Setup(t => t.StringIncrementAsync(It.IsAny<RedisKey>(), 1, CommandFlags.None))
            .ReturnsAsync(1);
        _transactionMock.Setup(t => t.ExecuteAsync(CommandFlags.None)).ThrowsAsync(fault);
    }
}
