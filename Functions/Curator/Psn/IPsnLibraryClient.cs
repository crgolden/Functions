namespace Functions.Curator.Psn;

public interface IPsnLibraryClient
{
    Task<IReadOnlyList<Entitlement>> EntitlementsAsync(
        PsnSession session,
        int? limit = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EntitlementDownloadSize>> DownloadSizesAsync(
        PsnSession session,
        CancellationToken cancellationToken = default);
}
