namespace Functions.Curator.OpenCritic;

public interface IOpenCriticRateLimiter
{
    Task AcquireAsync(OpenCriticCredential credential, CancellationToken cancellationToken = default);
}
