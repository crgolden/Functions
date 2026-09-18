namespace Functions.Curator.Rawg;

public interface IRawgRateLimiterFactory
{
    IRawgRateLimiter ForUser(Guid identitySub);

    IRawgRateLimiter ForAdmin();
}
