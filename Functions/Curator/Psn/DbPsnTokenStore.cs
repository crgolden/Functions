namespace Functions.Curator.Psn;

using System.Security.Cryptography;
using System.Text.Json;

public sealed class DbPsnTokenStore : IPsnTokenStore
{
    private readonly string _identitySub;
    private readonly PsnLinkRepository _repository;
    private readonly TokenCrypto _crypto;
    private readonly PsnAccessTokenCache? _accessTokenCache;

    public DbPsnTokenStore(
        string identitySub,
        PsnLinkRepository repository,
        TokenCrypto crypto,
        PsnAccessTokenCache? accessTokenCache = null)
    {
        _identitySub = identitySub;
        _repository = repository;
        _crypto = crypto;
        _accessTokenCache = accessTokenCache;
    }

    public async Task<PsnTokenResponse?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var link = await _repository.GetLinkAsync(_identitySub, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return null;
        }

        byte[] plaintext;
        try
        {
            plaintext = _crypto.Decrypt(link.TokenResponseEnc);
        }
        catch (CryptographicException)
        {
            return null;
        }

        PsnDurableToken? durable;
        try
        {
            durable = JsonSerializer.Deserialize<PsnDurableToken>(plaintext);
        }
        catch (JsonException)
        {
            return null;
        }

        if (durable is null)
        {
            return null;
        }

        var cached = _accessTokenCache is null
            ? null
            : await _accessTokenCache.LoadAsync(_identitySub, cancellationToken).ConfigureAwait(false);

        return new PsnTokenResponse
        {
            AccessToken = cached?.AccessToken,
            RefreshToken = durable.RefreshToken,
            ExpiresIn = cached?.ExpiresIn ?? 0,
            AccessTokenExpiresAt = cached?.AccessTokenExpiresAt ?? 0,
            RefreshTokenExpiresAt = durable.RefreshTokenExpiresAt,
        };
    }

    public async Task SaveAsync(PsnTokenResponse tokenResponse, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            return;
        }

        var durable = new PsnDurableToken
        {
            RefreshToken = tokenResponse.RefreshToken,
            RefreshTokenExpiresAt = tokenResponse.RefreshTokenExpiresAt,
        };
        var encrypted = _crypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(durable));
        var persisted = await _repository
            .UpdateTokenAsync(
                _identitySub,
                encrypted,
                tokenResponse.AccessTokenExpiresAt,
                tokenResponse.RefreshTokenExpiresAt,
                cancellationToken)
            .ConfigureAwait(false);

        if (!persisted)
        {
            throw new PsnAuthException(
                $"Refreshed PSN token for {_identitySub} was not persisted: the account has no psn_links row.");
        }

        if (_accessTokenCache is not null)
        {
            await _accessTokenCache
                .SaveAsync(_identitySub, tokenResponse, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "DbPsnTokenStore never unlinks an account -- that's an interactive Curator route, never reachable from a background job. A caller reaching this is a bug, not a no-op.");
}
