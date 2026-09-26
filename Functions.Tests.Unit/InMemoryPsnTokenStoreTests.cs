namespace Functions.Tests.Unit;

using Functions.Curator.Psn;

[Trait("Category", "Unit")]
public sealed class InMemoryPsnTokenStoreTests
{
    private static readonly int AccessTokenLifetimeSeconds = Generated.NewExpiresInSeconds();

    [Fact]
    public async Task RoundTripsASavedToken()
    {
        // Arrange
        var store = new InMemoryPsnTokenStore();
        var token = NewTokenResponse();

        // Act
        await store.SaveAsync(token, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(token, loaded);
    }

    [Fact]
    public async Task ClearAsync_RemovesTheHeldToken()
    {
        // Arrange
        var store = new InMemoryPsnTokenStore();
        await store.SaveAsync(
            NewTokenResponse(),
            TestContext.Current.CancellationToken);

        // Act
        await store.ClearAsync(TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task BeforeAnySave_LoadReturnsNull()
    {
        // Arrange
        var store = new InMemoryPsnTokenStore();

        // Act
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task IsNotSharedAcrossSeparateInstances_SoTheDefaultCannotSilentlyBecomePersistent()
    {
        // Arrange
        var first = new InMemoryPsnTokenStore();
        var second = new InMemoryPsnTokenStore();
        await first.SaveAsync(
            NewTokenResponse(),
            TestContext.Current.CancellationToken);

        // Act
        var loaded = await second.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    private static PsnTokenResponse NewTokenResponse() => new()
    {
        AccessToken = Generated.NewAccessToken(),
        ExpiresIn = AccessTokenLifetimeSeconds,
        AccessTokenExpiresAt = Generated.NewUtcTimestamp().ToUnixTimeSeconds(),
    };
}
