namespace Functions.Tests.Unit;

using System.Data;
using System.Text;
using System.Text.Json;
using Functions.Curator;
using Functions.Curator.Psn;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class DbPsnTokenStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static readonly int AccessTokenLifetimeSeconds = Generated.NewExpiresInSeconds();

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _timeProvider = new(Now);

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenNoLinkExists()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var store = NewStore(dataSource, NewCrypto());

        // Act
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenTheCiphertextWasEncryptedUnderADifferentKey()
    {
        // Arrange
        var otherCrypto = NewCrypto();
        var ciphertext = otherCrypto.Encrypt(DurableTokenBytes(Generated.NewRefreshToken()));
        var dataSource = LinkDataSource(ciphertext, harvestTrophies: false);
        var store = NewStore(dataSource, NewCrypto());

        // Act
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenTheDecryptedBytesAreNotValidJson()
    {
        // Arrange
        var crypto = NewCrypto();
        var ciphertext = crypto.Encrypt(Encoding.UTF8.GetBytes(NewMalformedJson()));
        var dataSource = LinkDataSource(ciphertext, harvestTrophies: false);
        var store = NewStore(dataSource, crypto);

        // Act
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenTheJsonRootIsNotAnObject()
    {
        // Arrange
        var crypto = NewCrypto();
        var ciphertext = crypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(new[] { Generated.NewRefreshToken() }));
        var dataSource = LinkDataSource(ciphertext, harvestTrophies: false);
        var store = NewStore(dataSource, crypto);

        // Act
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_ReturnsAnAlreadyExpiredAccessTokenAlongsideTheDurableRefreshToken()
    {
        // Arrange
        var crypto = NewCrypto();
        var refreshToken = Generated.NewRefreshToken();
        var ciphertext = crypto.Encrypt(DurableTokenBytes(refreshToken));
        var dataSource = LinkDataSource(ciphertext, harvestTrophies: false);
        var store = NewStore(dataSource, crypto);

        // Act
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(loaded);
        Assert.Null(loaded.AccessToken);
        Assert.Equal(refreshToken, loaded.RefreshToken);
        Assert.Equal(0, loaded.AccessTokenExpiresAt);
        Assert.True(loaded.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task LoadAsync_ParsesRefreshTokenExpiresAt_WhenPresent()
    {
        // Arrange
        var crypto = NewCrypto();
        var refreshTokenExpiresAt = (double)Generated.NewUtcTimestamp().ToUnixTimeSeconds();
        var ciphertext = crypto.Encrypt(
            DurableTokenBytes(Generated.NewRefreshToken(), refreshTokenExpiresAt));
        var dataSource = LinkDataSource(ciphertext, harvestTrophies: false);
        var store = NewStore(dataSource, crypto);

        // Act
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(refreshTokenExpiresAt, loaded?.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task SaveAsync_NoOps_WhenTheTokenHasNoAccessToken()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var store = NewStore(dataSource, NewCrypto());

        // Act
        await store.SaveAsync(
            new PsnTokenResponse
            {
                RefreshToken = Generated.NewRefreshToken(),
                ExpiresIn = AccessTokenLifetimeSeconds,
                AccessTokenExpiresAt = Now.ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task SaveAsync_EncryptsAndPersistsTheDurableRefreshTokenFields()
    {
        // Arrange
        var crypto = NewCrypto();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var store = NewStore(dataSource, crypto);

        var refreshToken = Generated.NewRefreshToken();
        var refreshTokenExpiresAt = (double)Generated.NewUtcTimestamp().ToUnixTimeSeconds();

        // Act
        await store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = Generated.NewAccessToken(),
                RefreshToken = refreshToken,
                ExpiresIn = AccessTokenLifetimeSeconds,
                AccessTokenExpiresAt = Now.ToUnixTimeSeconds(),
                RefreshTokenExpiresAt = refreshTokenExpiresAt,
            },
            TestContext.Current.CancellationToken);

        // Assert
        var command = dataSource.ExecutedCommands[0];
        var persisted = Assert.IsType<byte[]>(command.Parameters[CuratorSqlParameters.TokenResponseEnc].Value);
        var decrypted = JsonSerializer.Deserialize<PsnDurableToken>(crypto.Decrypt(persisted));
        Assert.Equal(refreshToken, decrypted?.RefreshToken);
        Assert.Equal(refreshTokenExpiresAt, decrypted?.RefreshTokenExpiresAt);
        Assert.Equal(Now, command.Parameters[CuratorSqlParameters.AccessTokenExpiresAt].Value);
    }

    [Fact]
    public async Task SaveAsync_NeverPutsAnEphemeralFieldInTheDurableBlob()
    {
        // Arrange
        var crypto = NewCrypto();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var store = NewStore(dataSource, crypto);

        // Act
        await store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = Generated.NewAccessToken(),
                RefreshToken = Generated.NewRefreshToken(),
                ExpiresIn = AccessTokenLifetimeSeconds,
                AccessTokenExpiresAt = Now.ToUnixTimeSeconds(),
                RefreshTokenExpiresAt = Generated.NewUtcTimestamp().ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);

        // Assert
        var persisted = Assert.IsType<byte[]>(dataSource.ExecutedCommands[0].Parameters[CuratorSqlParameters.TokenResponseEnc].Value);
        var decrypted = JsonDocument.Parse(crypto.Decrypt(persisted)).RootElement;
        Assert.Equal(
            [PsnDurableToken.RefreshTokenPropertyName, PsnDurableToken.RefreshTokenExpiresAtPropertyName],
            decrypted.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_OmitsRefreshTokenExpiresAt_WhenAbsent()
    {
        // Arrange
        var crypto = NewCrypto();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var store = NewStore(dataSource, crypto);

        // Act
        await store.SaveAsync(
            NewTokenResponse(),
            TestContext.Current.CancellationToken);

        // Assert
        var persisted = Assert.IsType<byte[]>(dataSource.ExecutedCommands[0].Parameters[CuratorSqlParameters.TokenResponseEnc].Value);
        var decrypted = JsonDocument.Parse(crypto.Decrypt(persisted)).RootElement;
        Assert.False(decrypted.TryGetProperty(PsnDurableToken.RefreshTokenExpiresAtPropertyName, out _));
    }

    [Fact]
    public async Task SaveAsync_Throws_WhenTheUpdateMatchesNoLinkRow()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(0));
        var identitySub = NewIdentitySub();
        var store = new DbPsnTokenStore(identitySub, new PsnLinkRepository(dataSource), NewCrypto());

        // Act
        var exception = await Record.ExceptionAsync(() => store.SaveAsync(
            NewTokenResponse(),
            TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<PsnAuthException>(exception);
        Assert.Contains(identitySub.ToString(), authException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearAsync_ThrowsNotSupported_BecauseNothingInAFunctionsJobShouldEverUnlinkAnAccount()
    {
        // Arrange
        var store = NewStore(new FakeDbDataSource(), NewCrypto());

        // Act
        var exception = await Record.ExceptionAsync(() => store.ClearAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }

    [Fact]
    public async Task LoadAsync_MergesTheCachedAccessTokenOverTheDurableRefreshToken()
    {
        // Arrange
        var crypto = NewCrypto();
        var identitySub = NewIdentitySub();
        var refreshToken = Generated.NewRefreshToken();
        var dataSource = LinkDataSource(crypto.Encrypt(DurableTokenBytes(refreshToken)), harvestTrophies: false);
        var cached = new PsnCachedAccessToken
        {
            AccessToken = Generated.NewAccessToken(),
            ExpiresIn = AccessTokenLifetimeSeconds,
            AccessTokenExpiresAt = Now.ToUnixTimeSeconds() + AccessTokenLifetimeSeconds,
        };
        _databaseMock
            .Setup(d => d.StringGetAsync(PsnAccessTokenCache.CacheKey(identitySub), CommandFlags.None))
            .ReturnsAsync(JsonSerializer.Serialize(cached));
        var store = new DbPsnTokenStore(identitySub, new PsnLinkRepository(dataSource), crypto, Cache());

        // Act
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(cached.AccessToken, loaded.AccessToken);
        Assert.Equal(refreshToken, loaded.RefreshToken);
        Assert.Equal(cached.AccessTokenExpiresAt, loaded.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task SaveAsync_CachesTheAccessTokenUnderTheIdentitySub_AfterTheRowIsPersisted()
    {
        // Arrange
        var identitySub = NewIdentitySub();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        _databaseMock
            .Setup(d => d.StringSetAsync(
                PsnAccessTokenCache.CacheKey(identitySub),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                CommandFlags.None))
            .ReturnsAsync(true);
        var store = new DbPsnTokenStore(identitySub, new PsnLinkRepository(dataSource), NewCrypto(), Cache());

        // Act
        await store.SaveAsync(
            NewTokenResponse(),
            TestContext.Current.CancellationToken);

        // Assert
        _databaseMock.VerifyAll();
    }

    [Fact]
    public async Task SaveAsync_CachesNothing_WhenTheUpdateMatchesNoLinkRow()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(0));
        var store = new DbPsnTokenStore(
            NewIdentitySub(), new PsnLinkRepository(dataSource), NewCrypto(), Cache());

        // Act
        await Assert.ThrowsAsync<PsnAuthException>(() => store.SaveAsync(
            NewTokenResponse(),
            TestContext.Current.CancellationToken));

        // Assert
        _databaseMock.VerifyNoOtherCalls();
    }

    private static PsnTokenResponse NewTokenResponse() => new()
    {
        AccessToken = Generated.NewAccessToken(),
        RefreshToken = Generated.NewRefreshToken(),
        ExpiresIn = AccessTokenLifetimeSeconds,
        AccessTokenExpiresAt = Now.ToUnixTimeSeconds() + AccessTokenLifetimeSeconds,
    };

    private static DbPsnTokenStore NewStore(FakeDbDataSource dataSource, TokenCrypto crypto) =>
        new(NewIdentitySub(), new PsnLinkRepository(dataSource), crypto);

    private static byte[] DurableTokenBytes(string refreshToken, double? refreshTokenExpiresAt = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new PsnDurableToken
        {
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = refreshTokenExpiresAt,
        });

    private static TokenCrypto NewCrypto() => new(Generated.NewWebSafeBase64Key(TokenCrypto.KeySizeBytes));

    private static FakeDbDataSource LinkDataSource(byte[] tokenResponseEnc, bool harvestTrophies)
    {
        var table = FakeResultSet.WithColumns(
            typeof(byte[]),
            typeof(bool));
        table.Rows.Add(tokenResponseEnc, harvestTrophies);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        return dataSource;
    }

    private PsnAccessTokenCache Cache() => new(_databaseMock.Object, _timeProvider);
}
