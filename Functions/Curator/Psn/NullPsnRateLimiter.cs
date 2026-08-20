namespace Functions.Curator.Psn;

public sealed class NullPsnRateLimiter : IPsnRateLimiter
{
    public static readonly NullPsnRateLimiter Unthrottled = new();

    public Task AcquireAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
