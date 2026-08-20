namespace Functions.Curator.Rawg;

public interface IRawgClient
{
    Task<IReadOnlyList<RawgCandidate>> SearchGamesAsync(
        string title,
        RawgCredential credential,
        int pageSize = 5,
        CancellationToken cancellationToken = default);

    Task ValidateKeyAsync(RawgCredential credential, CancellationToken cancellationToken = default);

    Task<RawgGameDetailResponse?> FetchDetailAsync(
        int rawgGameId,
        RawgCredential credential,
        CancellationToken cancellationToken = default);
}
