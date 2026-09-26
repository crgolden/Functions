namespace Functions.Curator.Rawg;

using System.Globalization;
using StackExchange.Redis;

public sealed class RedisRawgRateLimiter : IRawgRateLimiter
{
    public const string AdminKey = "curator:rawg:admin";

    public const string UserKeyPrefix = "curator:rawg:";

    public const int MonthlyRequestQuota = 20_000;

    public const string MonthKeyFormat = "yyyy-MM";

    public const int TtlMarginSeconds = 60;

    private readonly IDatabase _database;
    private readonly string _key;
    private readonly int _maxRequests;
    private readonly TimeProvider _timeProvider;

    public RedisRawgRateLimiter(
        IDatabase database,
        string key,
        int maxRequests = MonthlyRequestQuota,
        TimeProvider? timeProvider = null)
    {
        _database = database;
        _key = key;
        _maxRequests = maxRequests;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string KeyForUser(Guid identitySub) => $"{UserKeyPrefix}{identitySub:D}";

    public static string KeyForMonth(string key, DateTimeOffset instant) =>
        $"{key}:{instant.UtcDateTime.ToString(MonthKeyFormat, CultureInfo.InvariantCulture)}";

    public static DateTimeOffset StartOfNextMonth(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    }

    public async Task<double?> TryAcquireAsync()
    {
        var now = _timeProvider.GetUtcNow();
        var monthKey = (RedisKey)KeyForMonth(_key, now);
        var secondsUntilReset = (StartOfNextMonth(now) - now).TotalSeconds;

        var spent = await _database.StringIncrementAsync(monthKey).ConfigureAwait(false);

        if (spent == 1)
        {
            await _database
                .KeyExpireAsync(monthKey, TimeSpan.FromSeconds(secondsUntilReset + TtlMarginSeconds))
                .ConfigureAwait(false);
        }

        return spent > _maxRequests ? secondsUntilReset : null;
    }
}
