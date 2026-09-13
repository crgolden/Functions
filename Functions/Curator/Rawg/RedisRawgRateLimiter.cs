namespace Functions.Curator.Rawg;

using StackExchange.Redis;

public sealed class RedisRawgRateLimiter : IRawgRateLimiter
{
    public const string AdminKey = "curator:rawg:admin";

    public const string UserKeyPrefix = "curator:rawg:";

    public const int MonthlyRequestQuota = 20_000;

    public const double QuotaWindowSeconds = 30 * 24 * 60 * 60;

    public const int TtlMarginSeconds = 60;

    private readonly IDatabase _database;
    private readonly RedisKey _key;
    private readonly int _maxRequests;
    private readonly double _windowSeconds;
    private readonly TimeProvider _timeProvider;

    public RedisRawgRateLimiter(
        IDatabase database,
        string key,
        int maxRequests = MonthlyRequestQuota,
        double windowSeconds = QuotaWindowSeconds,
        TimeProvider? timeProvider = null)
    {
        _database = database;
        _key = key;
        _maxRequests = maxRequests;
        _windowSeconds = windowSeconds;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string KeyForUser(string identitySub) => $"{UserKeyPrefix}{identitySub}";

    public async Task<double?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        var now = UnixSeconds();
        var windowStart = now - _windowSeconds;
        await _database.SortedSetRemoveRangeByScoreAsync(_key, 0, windowStart).ConfigureAwait(false);

        var count = await _database.SortedSetLengthAsync(_key).ConfigureAwait(false);
        if (count >= _maxRequests)
        {
            var oldest = await _database
                .SortedSetRangeByRankWithScoresAsync(_key, 0, 0)
                .ConfigureAwait(false);
            if (oldest.Length > 0)
            {
                var wait = _windowSeconds - (now - oldest[0].Score);
                if (wait > 0)
                {
                    return wait;
                }
            }
        }

        await _database
            .SortedSetAddAsync(_key, Guid.NewGuid().ToString(), UnixSeconds())
            .ConfigureAwait(false);
        await _database
            .KeyExpireAsync(_key, TimeSpan.FromSeconds(_windowSeconds + TtlMarginSeconds))
            .ConfigureAwait(false);
        return null;
    }

    private double UnixSeconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
}
