namespace Functions.Tests.Unit;

using System.Text.Json;
using Curator.Psn;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;

[Trait("Category", "Unit")]
public sealed class PsnAccessTokenCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public void CacheKey_ScopesTheEntryToOneIdentitySub()
    {
        // Arrange
        var identitySub = Guid.NewGuid().ToString();

        // Act
        var key = PsnAccessTokenCache.CacheKey(identitySub);

        // Assert
        Assert.Equal($"{PsnAccessTokenCache.CacheKeyPrefix}{identitySub}", key);
        Assert.NotEqual(key, PsnAccessTokenCache.CacheKey(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenNothingIsCached()
    {
        // Arrange
        var identitySub = Guid.NewGuid().ToString();
        StubGet(identitySub, RedisValue.Null);

        // Act
        var loaded = await Cache().LoadAsync(identitySub, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenTheCachedPayloadIsNotValidJson()
    {
        // Arrange
        var identitySub = Guid.NewGuid().ToString();
        StubGet(identitySub, "not json");

        // Act
        var loaded = await Cache().LoadAsync(identitySub, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_ReturnsTheCachedEphemeralFields()
    {
        // Arrange
        var identitySub = Guid.NewGuid().ToString();
        var cached = new PsnCachedAccessToken
        {
            AccessToken = "AT",
            ExpiresIn = 3600,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds() + 3600,
        };
        StubGet(identitySub, JsonSerializer.Serialize(cached));

        // Act
        var loaded = await Cache().LoadAsync(identitySub, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(cached, loaded);
    }

    [Fact]
    public async Task SaveAsync_WritesNothing_WhenTheTokenCarriesNoAccessToken()
    {
        // Arrange
        var token = new PsnTokenResponse
        {
            RefreshToken = "RT",
            ExpiresIn = 3600,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds() + 3600,
        };

        // Act
        await Cache().SaveAsync(Guid.NewGuid().ToString(), token, TestContext.Current.CancellationToken);

        // Assert
        _databaseMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SaveAsync_WritesNothing_WhenTheAccessTokenHasAlreadyExpired()
    {
        // Arrange
        var token = new PsnTokenResponse
        {
            AccessToken = "AT",
            RefreshToken = "RT",
            ExpiresIn = 3600,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds(),
        };

        // Act
        await Cache().SaveAsync(Guid.NewGuid().ToString(), token, TestContext.Current.CancellationToken);

        // Assert
        _databaseMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SaveAsync_StoresOnlyTheEphemeralFieldsAndExpiresWithTheAccessToken()
    {
        // Arrange
        var identitySub = Guid.NewGuid().ToString();
        var token = new PsnTokenResponse
        {
            AccessToken = "AT",
            RefreshToken = "RT",
            ExpiresIn = 3600,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds() + 3600,
            RefreshTokenExpiresAt = Now.ToUnixTimeSeconds() + 5_000_000,
        };
        var written = RedisValue.Null;
        var expiry = default(Expiration);
        _databaseMock
            .Setup(d => d.StringSetAsync(
                PsnAccessTokenCache.CacheKey(identitySub),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                CommandFlags.None))
            .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>(
                (_, value, ttl, _, _) =>
                {
                    written = value;
                    expiry = ttl;
                })
            .ReturnsAsync(true);

        // Act
        await Cache().SaveAsync(identitySub, token, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(3600), expiry);
        var stored = JsonDocument.Parse(written.ToString()).RootElement;
        Assert.Equal("AT", stored.GetProperty("access_token").GetString());
        Assert.Equal(3600, stored.GetProperty("expires_in").GetDouble());
        Assert.Equal(Now.ToUnixTimeSeconds() + 3600, stored.GetProperty("access_token_expires_at").GetDouble());
        Assert.False(stored.TryGetProperty("refresh_token", out _));
        Assert.False(stored.TryGetProperty("refresh_token_expires_at", out _));
    }

    private PsnAccessTokenCache Cache() => new(_databaseMock.Object, _timeProvider);

    private void StubGet(string identitySub, RedisValue value) =>
        _databaseMock
            .Setup(d => d.StringGetAsync(PsnAccessTokenCache.CacheKey(identitySub), CommandFlags.None))
            .ReturnsAsync(value);
}
