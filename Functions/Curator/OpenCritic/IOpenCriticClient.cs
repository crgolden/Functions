namespace Functions.Curator.OpenCritic;

public interface IOpenCriticClient
{
    Task ValidateKeyAsync(OpenCriticCredential credential, CancellationToken cancellationToken = default);

    Task<OpenCriticPaginationResult> FetchPlatformGamesAsync(
        string platform,
        OpenCriticCredential credential,
        int startSkip = 0,
        int? maxPages = null,
        CancellationToken cancellationToken = default);
}
