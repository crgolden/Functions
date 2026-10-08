namespace Functions.Curator.OpenCritic;

public sealed class NullOpenCriticRateLimiter : IOpenCriticRateLimiter
{
    public static readonly NullOpenCriticRateLimiter Unthrottled = new();

    public Task AcquireAsync(OpenCriticCredential credential, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
