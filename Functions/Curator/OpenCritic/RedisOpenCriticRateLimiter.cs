namespace Functions.Curator.OpenCritic;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

public sealed class RedisOpenCriticRateLimiter : IOpenCriticRateLimiter
{
    public const string KeyPrefix = "curator:opencritic:ratelimit:";

    public const string SharedBudgetUnavailableEvent = "curator.opencritic.rate-limit-unavailable";

    internal const int SecondKeyTtlSeconds = 2;

    private readonly IDatabase _database;
    private readonly int _requestsPerSecond;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _localSlotsLock = new();
    private readonly Dictionary<string, DateTimeOffset> _nextLocalSlots = new(StringComparer.Ordinal);

    public RedisOpenCriticRateLimiter(IDatabase database, int requestsPerSecond, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerSecond);
        _database = database;
        _requestsPerSecond = requestsPerSecond;
        _timeProvider = timeProvider ?? TimeProvider.System;
        LocalSpacing = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / requestsPerSecond);
    }

    internal TimeSpan LocalSpacing { get; }

    public static string KeyForCredential(OpenCriticCredential credential) =>
        KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential.RapidApiKey)));

    public static string KeyForSecond(string credentialKey, long unixSecond) =>
        $"{credentialKey}:{unixSecond.ToString(CultureInfo.InvariantCulture)}";

    public async Task AcquireAsync(OpenCriticCredential credential, CancellationToken cancellationToken = default)
    {
        var credentialKey = KeyForCredential(credential);
        await WaitForLocalSlotAsync(credentialKey, cancellationToken).ConfigureAwait(false);
        while (!await TryTakeSharedSlotAsync(credentialKey).ConfigureAwait(false))
        {
            await Task.Delay(UntilTheNextSecond(), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsRedisUnreachable(Exception exception) => exception switch
    {
        RedisConnectionException => true,
        RedisTimeoutException => true,
        IOException => true,
        _ => false,
    };

    private Task WaitForLocalSlotAsync(string credentialKey, CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_localSlotsLock)
        {
            var now = _timeProvider.GetUtcNow();
            var start = _nextLocalSlots.TryGetValue(credentialKey, out var reserved) && reserved > now
                ? reserved
                : now;
            _nextLocalSlots[credentialKey] = start + LocalSpacing;
            delay = start - now;
        }

        return delay > TimeSpan.Zero
            ? Task.Delay(delay, _timeProvider, cancellationToken)
            : Task.CompletedTask;
    }

    private async Task<bool> TryTakeSharedSlotAsync(string credentialKey)
    {
        var secondKey = (RedisKey)KeyForSecond(credentialKey, _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        try
        {
            var transaction = _database.CreateTransaction();
            var taken = transaction.StringIncrementAsync(secondKey);
            _ = transaction.KeyExpireAsync(secondKey, TimeSpan.FromSeconds(SecondKeyTtlSeconds));
            await transaction.ExecuteAsync().ConfigureAwait(false);
            return await taken.ConfigureAwait(false) <= _requestsPerSecond;
        }
        catch (Exception exception) when (IsRedisUnreachable(exception))
        {
            Telemetry.Tracing.RecordHandledException(SharedBudgetUnavailableEvent, exception);
            return true;
        }
    }

    private TimeSpan UntilTheNextSecond()
    {
        var now = _timeProvider.GetUtcNow();
        return DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() + 1) - now;
    }
}
