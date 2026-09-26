namespace Functions.Curator.Rawg;

public interface IRawgRateLimiter
{
    Task<double?> TryAcquireAsync();
}
