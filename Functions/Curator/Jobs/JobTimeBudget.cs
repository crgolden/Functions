namespace Functions.Curator.Jobs;

public sealed class JobTimeBudget
{
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(20);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _budget;
    private readonly long _startedAt;

    public JobTimeBudget(TimeSpan? budget = null, TimeProvider? timeProvider = null)
    {
        _budget = budget ?? Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAt = _timeProvider.GetTimestamp();
    }

    public TimeSpan Elapsed => _timeProvider.GetElapsedTime(_startedAt);

    public bool Expired => Elapsed >= _budget;
}
