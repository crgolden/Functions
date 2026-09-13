namespace Functions.Curator.Rawg;

public interface IRawgRateLimiterFactory
{
    IRawgRateLimiter ForUser(string identitySub);

    IRawgRateLimiter ForAdmin();
}
