namespace Functions.Curator.Psn;

using System.Security.Cryptography;

public sealed class TokenCrypto
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int KeySizeBytes = 32;

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

        var token = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        nonce.CopyTo(token, 0);
        ciphertext.CopyTo(token, NonceSizeBytes);
        tag.CopyTo(token, NonceSizeBytes + ciphertext.Length);
        return token;
    }

    public byte[] Decrypt(byte[] token)
    {
        if (token.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Token is shorter than a nonce + tag.");
        }

        var nonce = token.AsSpan(0, NonceSizeBytes);
        var ciphertext = token.AsSpan(NonceSizeBytes, token.Length - NonceSizeBytes - TagSizeBytes);
        var tag = token.AsSpan(token.Length - TagSizeBytes, TagSizeBytes);

        var plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(_rawKey, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padded = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
