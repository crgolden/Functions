namespace Functions.Tests.Unit;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curator.Psn;

[Trait("Category", "Unit")]
public sealed class TokenCryptoTests
{
    private const string PythonGeneratedKey = "KOZEl3PXkk9i2iVoJw-cepA5qIsK1ZK56K2ykqXZ17U=";
    private const string PythonGeneratedTokenBase64 =
        "M6cqDHc7e8NuKxBHyAJZo1A4EIqL30HmNVAbbYNG0QWCuauJqFq9kKer7ezpzyv80HHYxkEkabsxZvRry7kobYXqC/fHwErXk2FkZwDaEraR9WO+RvSZTV3fAzgmyniKmDXu4YnXt/33EA==";

    private const string PythonGeneratedPlaintext =
        """{"refresh_token": "sample-refresh-token-value", "scope": "psn:mobile.v2.core"}""";

    [Fact]
    public void Decrypt_ReadsARealTokenEncryptedByCuratorsOwnPythonTokenCrypto()
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

    private static string GenerateKey()
    {
        var raw = new byte[32];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_');
    }
}
