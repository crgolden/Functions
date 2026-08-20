namespace Functions.Curator.Library;

using Psn;

public sealed class IngestionService
{
    public const string LiveSource = "curator-live";

    private readonly IPsnLibraryClient _libraryClient;
    private readonly EntitlementPullRepository _repository;

    public IngestionService(IPsnLibraryClient libraryClient, EntitlementPullRepository repository)
    {
        _libraryClient = libraryClient;
        _repository = repository;
    }

    public async Task<(string PullId, IReadOnlyList<EntitlementSnapshot> Snapshots)> IngestAsync(
        string identitySub,
        PsnSession session,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var entitlements = await _libraryClient
            .EntitlementsAsync(session, limit, cancellationToken)
            .ConfigureAwait(false);

        var snapshots = new List<EntitlementSnapshot>(entitlements.Count);
        foreach (var entitlement in entitlements)
        {
            var entitlementId = entitlement.EntitlementId;
            if (string.IsNullOrWhiteSpace(entitlementId))
            {
                continue;
            }

            snapshots.Add(ToSnapshot(entitlement, entitlementId));
        }

        var skipped = entitlements.Count - snapshots.Count;
        if (skipped > 0)
        {
            Telemetry.Tracing.RecordHandledFailure(
                "ingestion.entitlement-without-id",
                $"{skipped} of {entitlements.Count} entitlements carried no entitlementId and were not stored.");
        }

        var pullId = await _repository
            .RecordPullAsync(identitySub, LiveSource, snapshots, entitlements.Count, cancellationToken)
            .ConfigureAwait(false);
        return (pullId, snapshots);
    }

    private static EntitlementSnapshot ToSnapshot(Entitlement entitlement, string entitlementId) => new(entitlementId)
    {
        ConceptId = entitlement.ConceptId,
        ProductId = entitlement.ProductId,
        TitleId = entitlement.TitleId,
        GameMetaName = entitlement.GameMetaName,
        ConceptMetaName = entitlement.ConceptMetaName,
        TitleMetaName = entitlement.TitleMetaName,
        PackageType = entitlement.PackageType,
        Active = entitlement.Active,
        SkuId = entitlement.SkuId,
        ActiveDate = entitlement.ActiveDate,
        TitleImageUrl = entitlement.TitleImageUrl,
        GameIconUrl = entitlement.GameIconUrl,
        ConceptIconUrl = entitlement.ConceptIconUrl,
        IsGame = entitlement.IsGame,
        PlatformIds = entitlement.PlatformIds,
        Raw = entitlement.Raw,
    };
}
