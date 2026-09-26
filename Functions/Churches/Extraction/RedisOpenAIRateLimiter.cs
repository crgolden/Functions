namespace Functions.Churches.Extraction;

using System.Globalization;
using StackExchange.Redis;

public sealed class RedisOpenAIRateLimiter : IOpenAIRateLimiter
{
    public const string Key = "churches:openai:ratelimit";

    public const int DeploymentRequestsPerMinute = 200;

    public const int DeploymentTokensPerMinute = 200_000;

    public const double WindowSeconds = 60;

    public const int TtlMarginSeconds = 60;

    public const double MinSecondsBetweenCalls = WindowSeconds / DeploymentRequestsPerMinute;

    public const double LostRaceWaitSeconds = MinSecondsBetweenCalls;

    internal const char TokenCountSeparator = ':';

    private const string UnavailableMessage = "The OpenAI rate-limit gate could not be reached.";

    private readonly IDatabase _database;
    private readonly int _maxRequests;
    private readonly int _maxTokens;
    private readonly double _minSecondsBetweenCalls;
    private readonly TimeProvider _timeProvider;

    public RedisOpenAIRateLimiter(
        IDatabase database,
        int maxRequests = DeploymentRequestsPerMinute,
        int maxTokens = DeploymentTokensPerMinute,
        double minSecondsBetweenCalls = MinSecondsBetweenCalls,
        TimeProvider? timeProvider = null)
    {
        _database = database;
        _maxRequests = maxRequests;
        _maxTokens = maxTokens;
        _minSecondsBetweenCalls = minSecondsBetweenCalls;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string Member(Guid callId, int estimatedTokens) =>
        $"{callId:N}{TokenCountSeparator}{estimatedTokens.ToString(CultureInfo.InvariantCulture)}";

    public static int TokensOf(RedisValue member)
    {
        var text = member.ToString();
        return int.Parse(text.AsSpan(text.LastIndexOf(TokenCountSeparator) + 1), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    public async Task<double?> TryAcquireAsync(int estimatedTokens)
    {
        try
        {
            var now = UnixSeconds();
            await _database.SortedSetRemoveRangeByScoreAsync(Key, 0, now - WindowSeconds).ConfigureAwait(false);

            var entries = await _database.SortedSetRangeByRankWithScoresAsync(Key).ConfigureAwait(false);
            var wait = WaitForRoom(entries, estimatedTokens, now);
            if (wait is not null)
            {
                return wait;
            }

            var transaction = _database.CreateTransaction();
            transaction.AddCondition(Condition.SortedSetLengthEqual(Key, entries.Length));
            _ = transaction.SortedSetAddAsync(Key, Member(Guid.NewGuid(), estimatedTokens), now);
            _ = transaction.KeyExpireAsync(Key, TimeSpan.FromSeconds(WindowSeconds + TtlMarginSeconds));
            var admitted = await transaction.ExecuteAsync().ConfigureAwait(false);
            return admitted ? null : LostRaceWaitSeconds;
        }
        catch (Exception exception) when (IsGateUnreachable(exception))
        {
            throw new RateLimiterUnavailableException(UnavailableMessage, exception);
        }
    }

    internal static bool IsGateUnreachable(Exception exception) => exception switch
    {
        RedisConnectionException => true,
        RedisTimeoutException => true,
        IOException => true,
        OperationCanceledException => true,
        _ => false,
    };

    private double? WaitForRoom(SortedSetEntry[] entries, int estimatedTokens, double now)
    {
        var spacingWait = entries.Length == 0 ? 0 : _minSecondsBetweenCalls - (now - entries[^1].Score);
        var budgetWait = WaitForBudget(entries, estimatedTokens, now);
        var wait = Math.Max(spacingWait, budgetWait);
        return wait > 0 ? wait : null;
    }

    private double WaitForBudget(SortedSetEntry[] entries, int estimatedTokens, double now)
    {
        var requests = entries.Length;
        var tokens = entries.Sum(entry => (long)TokensOf(entry.Element));
        if (HasRoom(requests, tokens, estimatedTokens))
        {
            return 0;
        }

        foreach (var entry in entries)
        {
            requests--;
            tokens -= TokensOf(entry.Element);
            if (HasRoom(requests, tokens, estimatedTokens))
            {
                return WindowSeconds - (now - entry.Score);
            }
        }

        return WindowSeconds;
    }

    private bool HasRoom(int requests, long tokens, int estimatedTokens) =>
        requests < _maxRequests && tokens + estimatedTokens <= _maxTokens;

    private double UnixSeconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
}
