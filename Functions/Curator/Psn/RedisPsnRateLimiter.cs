namespace Functions.Curator.Psn;

using StackExchange.Redis;

public sealed class RedisPsnRateLimiter : IPsnRateLimiter
{
    public const string DefaultKey = "curator:psn:ratelimit";

    private readonly IDatabase _database;
    private readonly RedisKey _key;
    private readonly int _maxRequests;
    private readonly double _windowSeconds;
    private readonly TimeProvider _timeProvider;

    public RedisPsnRateLimiter(
        IDatabase database,
        string key = DefaultKey,
        int maxRequests = PsnSession.RateLimitMaxRequests,
        double windowSeconds = PsnSession.RateLimitWindowSeconds,
        TimeProvider? timeProvider = null)
    {
        _database = database;
        _key = key;
        _maxRequests = maxRequests;
        _windowSeconds = windowSeconds;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task AcquireAsync(CancellationToken cancellationToken = default)
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
                    await Task.Delay(TimeSpan.FromSeconds(wait), _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        await _database
            .SortedSetAddAsync(_key, Guid.NewGuid().ToString(), UnixSeconds())
            .ConfigureAwait(false);
        await _database
            .KeyExpireAsync(_key, TimeSpan.FromSeconds(_windowSeconds + 60))
            .ConfigureAwait(false);
    }

    private double UnixSeconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
}
