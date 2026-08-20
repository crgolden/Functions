namespace Functions.Curator.Psn;

public interface IPsnLibraryClient
{
    Task<IReadOnlyList<Entitlement>> EntitlementsAsync(
        PsnSession session,
        int? limit = null,
        CancellationToken cancellationToken = default);
}
