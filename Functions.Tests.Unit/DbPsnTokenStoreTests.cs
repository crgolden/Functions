namespace Functions.Tests.Unit;

using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Curator.Psn;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class DbPsnTokenStoreTests
{
    private const int AccessTokenLifetimeSeconds = 3600;

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

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
        var ciphertext = otherCrypto.Encrypt(DurableTokenBytes(TestValues.NewRefreshToken()));
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
        var ciphertext = crypto.Encrypt("not json"u8.ToArray());
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
        var ciphertext = crypto.Encrypt("[1, 2, 3]"u8.ToArray());
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
        var refreshToken = TestValues.NewRefreshToken();
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
        var refreshTokenExpiresAt = (double)TestValues.NewUtcTimestamp().ToUnixTimeSeconds();
        var ciphertext = crypto.Encrypt(
            DurableTokenBytes(TestValues.NewRefreshToken(), refreshTokenExpiresAt));
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
                RefreshToken = TestValues.NewRefreshToken(),
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

        var refreshToken = TestValues.NewRefreshToken();
        var refreshTokenExpiresAt = (double)TestValues.NewUtcTimestamp().ToUnixTimeSeconds();

        // Act
        await store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = TestValues.NewAccessToken(),
                RefreshToken = refreshToken,
                ExpiresIn = AccessTokenLifetimeSeconds,
                AccessTokenExpiresAt = Now.ToUnixTimeSeconds(),
                RefreshTokenExpiresAt = refreshTokenExpiresAt,
            },
            TestContext.Current.CancellationToken);

        // Assert
        var command = dataSource.ExecutedCommands[0];
        var persisted = command.ParameterValue<byte[]>("@token_response_enc");
        var decrypted = JsonDocument.Parse(crypto.Decrypt(persisted)).RootElement;
        Assert.Equal(refreshToken, decrypted.GetProperty("refresh_token").GetString());
        Assert.Equal(refreshTokenExpiresAt, decrypted.GetProperty("refresh_token_expires_at").GetDouble());
        Assert.Equal(Now, command.Parameters["@access_token_expires_at"].Value);
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
                AccessToken = TestValues.NewAccessToken(),
                RefreshToken = TestValues.NewRefreshToken(),
                ExpiresIn = AccessTokenLifetimeSeconds,
                AccessTokenExpiresAt = Now.ToUnixTimeSeconds(),
                RefreshTokenExpiresAt = TestValues.NewUtcTimestamp().ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);

        // Assert
        var persisted = dataSource.ExecutedCommands[0].ParameterValue<byte[]>("@token_response_enc");
        var decrypted = JsonDocument.Parse(crypto.Decrypt(persisted)).RootElement;
        Assert.Equal(
            ["refresh_token", "refresh_token_expires_at"],
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
        var persisted = dataSource.ExecutedCommands[0].ParameterValue<byte[]>("@token_response_enc");
        var decrypted = JsonDocument.Parse(crypto.Decrypt(persisted)).RootElement;
        Assert.False(decrypted.TryGetProperty("refresh_token_expires_at", out _));
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
        Assert.Contains(identitySub, authException.Message, StringComparison.Ordinal);
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
        var refreshToken = TestValues.NewRefreshToken();
        var dataSource = LinkDataSource(crypto.Encrypt(DurableTokenBytes(refreshToken)), harvestTrophies: false);
        var cached = new PsnCachedAccessToken
        {
            AccessToken = TestValues.NewAccessToken(),
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

    private static string NewIdentitySub() => TestValues.NewIdentitySub();

    private static PsnTokenResponse NewTokenResponse() => new()
    {
        AccessToken = TestValues.NewAccessToken(),
        RefreshToken = TestValues.NewRefreshToken(),
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

    private static TokenCrypto NewCrypto()
    {
        var raw = new byte[32];
        RandomNumberGenerator.Fill(raw);
        return new TokenCrypto(Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_'));
    }

    private static FakeDbDataSource LinkDataSource(byte[] tokenResponseEnc, bool harvestTrophies)
    {
        var table = new DataTable();
        table.Columns.Add("token_response_enc", typeof(byte[]));
        table.Columns.Add("harvest_trophies", typeof(bool));
        table.Rows.Add(tokenResponseEnc, harvestTrophies);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        return dataSource;
    }

    private PsnAccessTokenCache Cache() => new(_databaseMock.Object, _timeProvider);
}
