namespace Functions.Curator.Psn;

public interface IPsnTokenStore
{
    Task<PsnTokenResponse?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(PsnTokenResponse tokenResponse, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
