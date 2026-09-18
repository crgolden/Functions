namespace Functions.Tests.Unit;

using System.Text.Json;
using Curator.Psn;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PsnAccessTokenCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static readonly int AccessTokenLifetimeSeconds = TestValues.NewExpiresInSeconds();

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public void CacheKey_WritesTheIdentitySubInTheHyphenatedLowercaseFormCuratorsPythonCacheUses()
    {
        // Arrange
        var identitySub = TestValues.NewIdentitySub();
        var otherIdentitySub = TestValues.NewIdentitySub();

        // Act
        var key = PsnAccessTokenCache.CacheKey(identitySub);

        // Assert
        var keyedSub = key[PsnAccessTokenCache.CacheKeyPrefix.Length..];
        Assert.StartsWith(PsnAccessTokenCache.CacheKeyPrefix, key, StringComparison.Ordinal);
        Assert.Matches(PythonUuidTextFixtureConstants.StrUuidPattern, keyedSub);
        Assert.Equal(identitySub, Guid.Parse(keyedSub));
        Assert.NotEqual(key, PsnAccessTokenCache.CacheKey(otherIdentitySub));
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenNothingIsCached()
    {
        // Arrange
        var identitySub = TestValues.NewIdentitySub();
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
        var identitySub = TestValues.NewIdentitySub();
        StubGet(identitySub, TestValues.NewMalformedJson());

        // Act
        var loaded = await Cache().LoadAsync(identitySub, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_ReturnsTheCachedEphemeralFields()
    {
        // Arrange
        var identitySub = TestValues.NewIdentitySub();
        var cached = new PsnCachedAccessToken
        {
            AccessToken = TestValues.NewAccessToken(),
            ExpiresIn = AccessTokenLifetimeSeconds,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds() + AccessTokenLifetimeSeconds,
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
            RefreshToken = TestValues.NewRefreshToken(),
            ExpiresIn = AccessTokenLifetimeSeconds,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds() + AccessTokenLifetimeSeconds,
        };

        // Act
        await Cache().SaveAsync(TestValues.NewIdentitySub(), token, TestContext.Current.CancellationToken);

        // Assert
        _databaseMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SaveAsync_WritesNothing_WhenTheAccessTokenHasAlreadyExpired()
    {
        // Arrange
        var token = new PsnTokenResponse
        {
            AccessToken = TestValues.NewAccessToken(),
            RefreshToken = TestValues.NewRefreshToken(),
            ExpiresIn = AccessTokenLifetimeSeconds,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds(),
        };

        // Act
        await Cache().SaveAsync(TestValues.NewIdentitySub(), token, TestContext.Current.CancellationToken);

        // Assert
        _databaseMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SaveAsync_StoresOnlyTheEphemeralFieldsAndExpiresWithTheAccessToken()
    {
        // Arrange
        var identitySub = TestValues.NewIdentitySub();
        var accessToken = TestValues.NewAccessToken();
        var token = new PsnTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = TestValues.NewRefreshToken(),
            ExpiresIn = AccessTokenLifetimeSeconds,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds() + AccessTokenLifetimeSeconds,
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
        Assert.Equal(TimeSpan.FromSeconds(AccessTokenLifetimeSeconds), expiry);
        var stored = JsonDocument.Parse(written.ToString()).RootElement;
        Assert.Equal(accessToken, stored.GetProperty(PsnCachedAccessToken.AccessTokenPropertyName).GetString());
        Assert.Equal(AccessTokenLifetimeSeconds, stored.GetProperty(PsnCachedAccessToken.ExpiresInPropertyName).GetDouble());
        Assert.Equal(
            Now.ToUnixTimeSeconds() + AccessTokenLifetimeSeconds,
            stored.GetProperty(PsnCachedAccessToken.AccessTokenExpiresAtPropertyName).GetDouble());
        Assert.False(stored.TryGetProperty(PsnDurableToken.RefreshTokenPropertyName, out _));
        Assert.False(stored.TryGetProperty(PsnDurableToken.RefreshTokenExpiresAtPropertyName, out _));
    }

    [Fact]
    public void CachedPropertyNames_AreTheSnakeCaseKeysCuratorsPythonCacheShares()
    {
        // Act
        string[] propertyNames =
        [
            PsnCachedAccessToken.AccessTokenPropertyName,
            PsnCachedAccessToken.ExpiresInPropertyName,
            PsnCachedAccessToken.AccessTokenExpiresAtPropertyName,
        ];

        // Assert
        Assert.Equal(["access_token", "expires_in", "access_token_expires_at"], propertyNames);
    }

    private PsnAccessTokenCache Cache() => new(_databaseMock.Object, _timeProvider);

    private void StubGet(Guid identitySub, RedisValue value) =>
        _databaseMock
            .Setup(d => d.StringGetAsync(PsnAccessTokenCache.CacheKey(identitySub), CommandFlags.None))
            .ReturnsAsync(value);
}
