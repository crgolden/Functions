namespace Functions.Tests.Unit;

using Curator.Enrichment;
using Curator.Rawg;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class RateLimitedRawgClientTests
{
    private readonly Mock<IRawgClient> _innerMock = new(MockBehavior.Strict);
    private readonly Mock<IRawgRateLimiter> _limiterMock = new(MockBehavior.Strict);
    private readonly RawgCredential _credential = new() { ApiKey = TestValues.NewRawgApiKey() };

    [Fact]
    public async Task SearchGamesAsync_SpendsOneBudgetEntryThenDelegates_WhenTheBudgetHasRoom()
    {
        // Arrange
        var title = TestValues.NewGameTitle();
        var candidates = new List<RawgCandidate>();
        _limiterMock.Setup(l => l.TryAcquireAsync(It.IsAny<CancellationToken>())).ReturnsAsync((double?)null);
        _innerMock
            .Setup(c => c.SearchGamesAsync(title, _credential, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates);

        // Act
        var result = await Client().SearchGamesAsync(title, _credential, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(candidates, result);
        _limiterMock.Verify(l => l.TryAcquireAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FetchDetailAsync_ThrowsARawgRateLimitCarryingTheWait_AndNeverCallsRawg_WhenTheBudgetIsSpent()
    {
        // Arrange
        var retryAfterSeconds = (double)TestValues.NewRetryAfterSeconds();
        _limiterMock.Setup(l => l.TryAcquireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(retryAfterSeconds);

        // Act
        var exception = await Record.ExceptionAsync(
            () => Client().FetchDetailAsync(TestValues.NewRawgGameId(), _credential, TestContext.Current.CancellationToken));

        // Assert
        var rateLimit = Assert.IsType<EnrichmentRateLimitException>(exception);
        Assert.Equal(EnrichmentProvider.Rawg, rateLimit.Provider);
        Assert.Equal(retryAfterSeconds, rateLimit.RetryAfterSeconds);
        _innerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ValidateKeyAsync_IsMeteredLikeEveryOtherCall_BecauseRawgChargesForIt()
    {
        // Arrange
        _limiterMock.Setup(l => l.TryAcquireAsync(It.IsAny<CancellationToken>())).ReturnsAsync((double?)null);
        _innerMock.Setup(c => c.ValidateKeyAsync(_credential, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await Client().ValidateKeyAsync(_credential, TestContext.Current.CancellationToken);

        // Assert
        _limiterMock.Verify(l => l.TryAcquireAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private RateLimitedRawgClient Client() => new(_innerMock.Object, _limiterMock.Object);
}
