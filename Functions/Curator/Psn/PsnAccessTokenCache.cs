namespace Functions.Curator.Psn;

using System.Text.Json;
using StackExchange.Redis;

public sealed class PsnAccessTokenCache
{
    public const string CacheKeyPrefix = "curator:psn:access_token:";

    private readonly IDatabase _database;
    private readonly TimeProvider _timeProvider;

    public PsnAccessTokenCache(IDatabase database, TimeProvider? timeProvider = null)
    {
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string CacheKey(string identitySub) => $"{CacheKeyPrefix}{identitySub}";

    public async Task<PsnCachedAccessToken?> LoadAsync(string identitySub, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = await _database.StringGetAsync(CacheKey(identitySub)).ConfigureAwait(false);
        if (cached.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PsnCachedAccessToken>(cached.ToString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        string identitySub,
        PsnTokenResponse tokenResponse,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var ttlSeconds = (int)(tokenResponse.AccessTokenExpiresAt - now);
        if (ttlSeconds <= 0)
        {
            return;
        }

        var ephemeral = new PsnCachedAccessToken
        {
            AccessToken = tokenResponse.AccessToken,
            ExpiresIn = tokenResponse.ExpiresIn,
            AccessTokenExpiresAt = tokenResponse.AccessTokenExpiresAt,
        };
        await _database
            .StringSetAsync(
                CacheKey(identitySub),
                JsonSerializer.Serialize(ephemeral),
                TimeSpan.FromSeconds(ttlSeconds))
            .ConfigureAwait(false);
    }
}
