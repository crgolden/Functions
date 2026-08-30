namespace Functions.Curator.Psn;

using System.Security.Cryptography;

public sealed class TokenCrypto
{
    internal const byte SchemeAesGcmV1 = 0x01;

    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int KeySizeBytes = 32;
    private const int SchemeSizeBytes = 1;

    private const string NotBase64UrlMessage = "TokenCrypto key is not valid base64url.";

    private readonly byte[] _rawKey;

    public TokenCrypto(string base64UrlKey)
    {
        var rawKey = DecodeBase64Url(base64UrlKey);
        if (rawKey.Length != KeySizeBytes)
        {
            throw new ArgumentException(
                $"TokenCrypto key must decode to {KeySizeBytes} bytes, got {rawKey.Length}.", nameof(base64UrlKey));
        }

        _rawKey = rawKey;
    }

    public byte[] Encrypt(byte[] data)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[data.Length];
        var tag = new byte[TagSizeBytes];
        using var aesGcm = new AesGcm(_rawKey, TagSizeBytes);
        aesGcm.Encrypt(nonce, data, ciphertext, tag);

        var token = new byte[SchemeSizeBytes + NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        token[0] = SchemeAesGcmV1;
        nonce.CopyTo(token, SchemeSizeBytes);
        ciphertext.CopyTo(token, SchemeSizeBytes + NonceSizeBytes);
        tag.CopyTo(token, SchemeSizeBytes + NonceSizeBytes + ciphertext.Length);
        return token;
    }

    public byte[] Decrypt(byte[] token)
    {
        if (TryDecryptSchemeAesGcmV1(token, out var plaintext))
        {
            return plaintext;
        }

        if (token.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Token is shorter than a nonce + tag.");
        }

        return DecryptNonceCiphertextTag(token);
    }

    private static byte[] DecodeBase64Url(string base64UrlKey)
    {
        var normalized = base64UrlKey.Replace('-', '+').Replace('_', '/');
        if (normalized.Length % 4 == 1 || !normalized.All(IsBase64Character))
        {
            throw new ArgumentException(NotBase64UrlMessage, nameof(base64UrlKey));
        }

        var padded = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException exc)
        {
            throw new ArgumentException(NotBase64UrlMessage, nameof(base64UrlKey), exc);
        }
    }

    private static bool IsBase64Character(char candidate) =>
        char.IsAsciiLetterOrDigit(candidate) || candidate is '+' or '/' or '=';

    private bool TryDecryptSchemeAesGcmV1(byte[] token, out byte[] plaintext)
    {
        plaintext = [];
        if (token.Length < SchemeSizeBytes + NonceSizeBytes + TagSizeBytes || token[0] != SchemeAesGcmV1)
        {
            return false;
        }

        try
        {
            plaintext = DecryptNonceCiphertextTag(token.AsSpan(SchemeSizeBytes));
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    private byte[] DecryptNonceCiphertextTag(ReadOnlySpan<byte> framed)
    {
        var nonce = framed[..NonceSizeBytes];
        var ciphertext = framed[NonceSizeBytes..^TagSizeBytes];
        var tag = framed[^TagSizeBytes..];

        var plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(_rawKey, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
