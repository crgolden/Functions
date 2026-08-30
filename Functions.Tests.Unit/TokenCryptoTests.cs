namespace Functions.Tests.Unit;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curator.Psn;

[Trait("Category", "Unit")]
public sealed class TokenCryptoTests
{
    private const int AesGcmNonceSizeBytes = 12;
    private const int AesGcmTagSizeBytes = 16;
    private const int AesGcmKeySizeBytes = 32;
    private const int SchemeSizeBytes = 1;

    private const byte NonCollidingFirstNonceByte = 0x00;

    private const string PythonGeneratedKey = "KOZEl3PXkk9i2iVoJw-cepA5qIsK1ZK56K2ykqXZ17U=";
    private const string PythonGeneratedTokenBase64 =
        "M6cqDHc7e8NuKxBHyAJZo1A4EIqL30HmNVAbbYNG0QWCuauJqFq9kKer7ezpzyv80HHYxkEkabsxZvRry7kobYXqC/fHwErXk2FkZwDaEraR9WO+RvSZTV3fAzgmyniKmDXu4YnXt/33EA==";

    private const string PythonGeneratedPlaintext =
        """{"refresh_token": "sample-refresh-token-value", "scope": "psn:mobile.v2.core"}""";

    [Fact]
    public void Decrypt_ReadsAnUnversionedTokenEncryptedByCuratorsOwnPythonTokenCrypto()
    {
        // Arrange
        var crypto = new TokenCrypto(PythonGeneratedKey);
        var token = Convert.FromBase64String(PythonGeneratedTokenBase64);

        // Act
        var plaintext = crypto.Decrypt(token);

        // Assert
        Assert.Equal(PythonGeneratedPlaintext, Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void Decrypt_ReadsAVersionedTokenWhoseBodyCuratorsOwnPythonTokenCryptoEncrypted()
    {
        // Arrange
        var crypto = new TokenCrypto(PythonGeneratedKey);
        var versioned = PrependSchemeByte(
            TokenCrypto.SchemeAesGcmV1, Convert.FromBase64String(PythonGeneratedTokenBase64));

        // Act
        var plaintext = crypto.Decrypt(versioned);

        // Assert
        Assert.Equal(PythonGeneratedPlaintext, Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void Decrypt_ReadsAVersionedToken_WhenTheSchemeByteLeadsTheUnversionedFraming()
    {
        // Arrange
        var rawKey = NewRawKey();
        var crypto = new TokenCrypto(ToBase64Url(rawKey));
        var plaintext = Encoding.UTF8.GetBytes(NewSecret());
        var versioned = PrependSchemeByte(
            TokenCrypto.SchemeAesGcmV1, UnversionedToken(rawKey, plaintext, NonCollidingFirstNonceByte));

        // Act
        var decrypted = crypto.Decrypt(versioned);

        // Assert
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_StillReadsAnUnversionedToken_WhenItsFirstNonceByteCollidesWithTheSchemeByte()
    {
        // Arrange
        var rawKey = NewRawKey();
        var crypto = new TokenCrypto(ToBase64Url(rawKey));
        var plaintext = Encoding.UTF8.GetBytes(NewSecret());
        var collidingToken = UnversionedToken(rawKey, plaintext, TokenCrypto.SchemeAesGcmV1);

        // Act
        var decrypted = crypto.Decrypt(collidingToken);

        // Assert
        Assert.Equal(TokenCrypto.SchemeAesGcmV1, collidingToken[0]);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_StillReadsTheShortestUnversionedToken_WhenItsFirstNonceByteCollidesWithTheSchemeByte()
    {
        // Arrange
        const int unversionedFramingOverheadBytes = AesGcmNonceSizeBytes + AesGcmTagSizeBytes;
        var rawKey = NewRawKey();
        var crypto = new TokenCrypto(ToBase64Url(rawKey));
        var colliding = UnversionedToken(rawKey, [], TokenCrypto.SchemeAesGcmV1);

        // Act
        var decrypted = crypto.Decrypt(colliding);

        // Assert
        Assert.Equal(unversionedFramingOverheadBytes, colliding.Length);
        Assert.Equal(TokenCrypto.SchemeAesGcmV1, colliding[0]);
        Assert.Empty(decrypted);
    }

    [Fact]
    public void Decrypt_ThrowsCryptographicException_WhenAVersionedTokenIsTampered()
    {
        // Arrange
        var crypto = new TokenCrypto(GenerateKey());
        var versioned = crypto.Encrypt(Encoding.UTF8.GetBytes(NewSecret()));
        versioned[^1] ^= 0xFF;

        // Act
        var exception = Record.Exception(() => crypto.Decrypt(versioned));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(exception);
    }

    [Fact]
    public void Encrypt_WritesTheVersionedFraming_LedByTheSchemeByte()
    {
        // Arrange
        const int versionedFramingOverheadBytes =
            SchemeSizeBytes + AesGcmNonceSizeBytes + AesGcmTagSizeBytes;
        var crypto = new TokenCrypto(GenerateKey());
        var plaintext = Encoding.UTF8.GetBytes(NewSecret());

        // Act
        var token = crypto.Encrypt(plaintext);

        // Assert
        Assert.Equal(plaintext.Length + versionedFramingOverheadBytes, token.Length);
        Assert.Equal(TokenCrypto.SchemeAesGcmV1, token[0]);
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTripsWithinDotNet()
    {
        // Arrange
        var crypto = new TokenCrypto(GenerateKey());
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new PsnDurableToken { RefreshToken = "RT" });

        // Act
        var token = crypto.Encrypt(plaintext);

        // Assert
        Assert.NotEqual(plaintext, token);
        Assert.Equal(plaintext, crypto.Decrypt(token));
    }

    [Fact]
    public void Decrypt_ThrowsCryptographicException_WhenTheKeyDoesNotMatch()
    {
        // Arrange
        var encryptor = new TokenCrypto(GenerateKey());
        var decryptor = new TokenCrypto(GenerateKey());
        var token = encryptor.Encrypt(Encoding.UTF8.GetBytes("secret"));

        // Act
        var exception = Record.Exception(() => decryptor.Decrypt(token));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(exception);
    }

    [Fact]
    public void Decrypt_ThrowsCryptographicException_WhenTheCiphertextIsTampered()
    {
        // Arrange
        var crypto = new TokenCrypto(GenerateKey());
        var token = crypto.Encrypt(Encoding.UTF8.GetBytes("secret"));
        token[^1] ^= 0xFF;

        // Act
        var exception = Record.Exception(() => crypto.Decrypt(token));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(exception);
    }

    [Fact]
    public void Decrypt_ThrowsCryptographicException_WhenTheTokenIsShorterThanANoncePlusTag()
    {
        // Arrange
        var crypto = new TokenCrypto(GenerateKey());

        // Act
        var exception = Record.Exception(() => crypto.Decrypt([1, 2, 3]));

        // Assert
        Assert.IsAssignableFrom<CryptographicException>(exception);
    }

    [Fact]
    public void Constructor_RejectsAKeyThatDoesNotDecodeTo32Bytes()
    {
        // Arrange
        var shortKey = Convert.ToBase64String(new byte[16]);

        // Act
        var exception = Record.Exception(() => new TokenCrypto(shortKey));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void Constructor_AcceptsAnUnpaddedKey_AndLandsOnTheSameBytesAsThePaddedForm()
    {
        // Arrange
        var rawKey = NewRawKey();
        var padded = ToBase64Url(rawKey);
        var unpadded = padded.TrimEnd('=');
        var plaintext = Encoding.UTF8.GetBytes(NewSecret());

        // Act
        var roundTripped = new TokenCrypto(unpadded).Decrypt(new TokenCrypto(padded).Encrypt(plaintext));

        // Assert
        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void Constructor_RejectsAKeyOneCharacterPastAMultipleOfFour_AsCuratorsPythonPortDoes()
    {
        // Arrange
        var overPadded = GenerateKey() + "=";

        // Act
        var exception = Record.Exception(() => new TokenCrypto(overPadded));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('\n')]
    [InlineData('!')]
    public void Constructor_RejectsAKeyCarryingAStrayCharacter_RatherThanDecodingItToOtherBytes(char stray)
    {
        // Arrange
        var key = GenerateKey();
        var strayInside = string.Concat(key[..8], stray, key[9..]);

        // Act
        var exception = Record.Exception(() => new TokenCrypto(strayInside));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    private static string GenerateKey() => ToBase64Url(NewRawKey());

    private static byte[] NewRawKey()
    {
        var raw = new byte[AesGcmKeySizeBytes];
        RandomNumberGenerator.Fill(raw);
        return raw;
    }

    private static string ToBase64Url(byte[] raw) =>
        Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_');

    private static string NewSecret() => $"secret-{Guid.NewGuid():N}";

    private static byte[] PrependSchemeByte(byte scheme, byte[] unversioned) => [scheme, .. unversioned];

    private static byte[] UnversionedToken(byte[] rawKey, byte[] plaintext, byte firstNonceByte)
    {
        var nonce = new byte[AesGcmNonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);
        nonce[0] = firstNonceByte;

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcmTagSizeBytes];
        using var aesGcm = new AesGcm(rawKey, AesGcmTagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. nonce, .. ciphertext, .. tag];
    }
}
