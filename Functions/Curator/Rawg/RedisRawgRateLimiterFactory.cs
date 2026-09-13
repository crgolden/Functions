namespace Functions.Curator.Rawg;

using StackExchange.Redis;

public sealed class RedisRawgRateLimiterFactory : IRawgRateLimiterFactory
{
    private readonly IConnectionMultiplexer _multiplexer;

    public RedisRawgRateLimiterFactory(IConnectionMultiplexer multiplexer) => _multiplexer = multiplexer;

    public IRawgRateLimiter ForUser(string identitySub) =>
        new RedisRawgRateLimiter(_multiplexer.GetDatabase(), RedisRawgRateLimiter.KeyForUser(identitySub));

    public IRawgRateLimiter ForAdmin() =>
        new RedisRawgRateLimiter(_multiplexer.GetDatabase(), RedisRawgRateLimiter.AdminKey);
}
