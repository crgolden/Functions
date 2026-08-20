namespace Functions.Curator.Psn;

public sealed class InMemoryPsnTokenStore : IPsnTokenStore
{
    private PsnTokenResponse? _token;

    public Task<PsnTokenResponse?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_token);

    public Task SaveAsync(PsnTokenResponse tokenResponse, CancellationToken cancellationToken = default)
    {
        _token = tokenResponse;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _token = null;
        return Task.CompletedTask;
    }
}
