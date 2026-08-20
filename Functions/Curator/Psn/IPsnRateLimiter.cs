namespace Functions.Curator.Psn;

public interface IPsnRateLimiter
{
    Task AcquireAsync(CancellationToken cancellationToken = default);
}
