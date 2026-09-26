namespace Functions.Tests.Integration;

using System.Text.Json.Nodes;
using Functions.Curator.Library;
using Functions.Curator.Psn;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class EntitlementPullRepositoryTests : IAsyncLifetime
{
    private const int OneSnapshot = 1;
    private const int NoSnapshots = 0;

    private const string SnapshotsForIdentitySql =
        "FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string EntitlementIdSql =
        $"SELECT {EntitlementSnapshotColumns.EntitlementId} {SnapshotsForIdentitySql}";

    private const string PullIdSql = $"SELECT pull_id {SnapshotsForIdentitySql}";

    private const string ActiveDateSql =
        $"SELECT {EntitlementSnapshotColumns.ActiveDate} {SnapshotsForIdentitySql}";

    private const string PlatformIdsSql =
        $"SELECT {EntitlementSnapshotColumns.PlatformIds} {SnapshotsForIdentitySql}";

    private const string PackageTypeSql =
        $"SELECT {EntitlementSnapshotColumns.PackageType} {SnapshotsForIdentitySql}";

    private const string TitleImageSql =
        $"SELECT {EntitlementSnapshotColumns.TitleImageUrl} {SnapshotsForIdentitySql}";

    private const string SnapshotCountSql = $"SELECT count(*) {SnapshotsForIdentitySql}";

    private const string EntryCountSql =
        "SELECT entry_count FROM entitlement_pulls WHERE pull_id = $1";

    private readonly CuratorDatabase _database;
    private Guid _identitySub;

    public EntitlementPullRepositoryTests(CuratorDatabase database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _identitySub = await _database.CreateUserAsync(Token);

    public async ValueTask DisposeAsync() => await _database.DeleteUserAsync(_identitySub, Token);

    [Fact]
    public async Task RecordPullAsync_WithAFullSnapshot_RoundTripsEveryColumnThroughJsonbToRecordset()
    {
        var activeDate = Generated.NewUtcTimestampAtSecondPrecision();
        var entitlementId = Generated.NewEntitlementId();
        var nestedBlockName = Generated.NewJsonPropertyName();
        var nestedPropertyName = Generated.NewJsonPropertyName();
        var nestedPropertyValue = Generated.LowercaseToken(8);
        var snapshot = new EntitlementSnapshot(entitlementId)
        {
            ConceptId = Generated.NewConceptId(),
            ProductId = Generated.NewProductId(),
            SkuId = Generated.NewSkuId(),
            TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix),
            GameMetaName = Generated.NewGameName(),
            ConceptMetaName = Generated.NewGameName(),
            TitleMetaName = Generated.NewGameName(),
            PackageType = Generated.NewPackageType(),
            Active = true,
            ActiveDate = activeDate,
            TitleImageUrl = Generated.NewCoverImageUri(),
            GameIconUrl = Generated.NewCoverImageUri(),
            ConceptIconUrl = Generated.NewCoverImageUri(),
            IsGame = true,
            PlatformIds = [Generated.NewPlatformId(), Generated.NewPlatformId()],
            Raw = new JsonObject
            {
                [nestedBlockName] = new JsonObject { [nestedPropertyName] = nestedPropertyValue },
            }.ToJsonString(),
        };
        var repository = new EntitlementPullRepository(_database.DataSource);
        var nestedRawSql =
            $"SELECT {EntitlementSnapshotColumns.Raw} -> '{nestedBlockName}' ->> '{nestedPropertyName}' {SnapshotsForIdentitySql}";

        var pullId = await repository.RecordPullAsync(
            _identitySub, IngestionService.LiveSource, [snapshot], OneSnapshot, Token);

        var storedEntitlementId = await _database.ScalarAsync<string>(EntitlementIdSql, Token, _identitySub);
        var storedPullId = await _database.ScalarAsync<Guid>(PullIdSql, Token, _identitySub);
        var storedActiveDate = await _database.ScalarAsync<DateTime>(ActiveDateSql, Token, _identitySub);
        var storedPlatformIds = await _database.ScalarAsync<string[]>(PlatformIdsSql, Token, _identitySub);
        var storedNestedRaw = await _database.ScalarAsync<string>(nestedRawSql, Token, _identitySub);

        Assert.Equal(entitlementId, storedEntitlementId);
        Assert.Equal(pullId, storedPullId);
        Assert.Equal(activeDate.UtcDateTime, storedActiveDate);
        Assert.Equal(snapshot.PlatformIds, storedPlatformIds);
        Assert.Equal(nestedPropertyValue, storedNestedRaw);
    }

    [Fact]
    public async Task RecordPullAsync_RepullingTheSameEntitlement_UpdatesTheRowRatherThanInsertingASecond()
    {
        var entitlementId = Generated.NewEntitlementId();
        var repository = new EntitlementPullRepository(_database.DataSource);
        var repulledPackageType = Generated.NewPackageType();
        var first = new EntitlementSnapshot(entitlementId)
        {
            PackageType = Generated.NewPackageType(),
            PlatformIds = [Generated.NewPlatformId()],
        };
        var second = new EntitlementSnapshot(entitlementId)
        {
            PackageType = repulledPackageType,
            PlatformIds = [Generated.NewPlatformId()],
        };
        await repository.RecordPullAsync(
            _identitySub, IngestionService.LiveSource, [first], OneSnapshot, Token);

        await repository.RecordPullAsync(
            _identitySub, IngestionService.LiveSource, [second], OneSnapshot, Token);

        var rowCount = await _database.ScalarAsync<long>(SnapshotCountSql, Token, _identitySub);
        var packageType = await _database.ScalarAsync<string>(PackageTypeSql, Token, _identitySub);

        Assert.Equal(OneSnapshot, rowCount);
        Assert.Equal(repulledPackageType, packageType);
    }

    [Fact]
    public async Task RecordPullAsync_WhenARepullCarriesNoRawOrArtwork_KeepsWhatTheEarlierPullStored()
    {
        var entitlementId = Generated.NewEntitlementId();
        var repository = new EntitlementPullRepository(_database.DataSource);
        var originalTitleImageUrl = Generated.NewCoverImageUri();
        var keptPropertyName = Generated.NewJsonPropertyName();
        var keptPropertyValue = Generated.LowercaseToken(8);
        var platformIds = new[] { Generated.NewPlatformId() };
        var original = new EntitlementSnapshot(entitlementId)
        {
            TitleImageUrl = originalTitleImageUrl,
            PlatformIds = platformIds,
            Raw = new JsonObject { [keptPropertyName] = keptPropertyValue }.ToJsonString(),
        };
        var sparse = new EntitlementSnapshot(entitlementId) { PlatformIds = platformIds };
        var keptRawSql =
            $"SELECT {EntitlementSnapshotColumns.Raw} ->> '{keptPropertyName}' {SnapshotsForIdentitySql}";
        await repository.RecordPullAsync(
            _identitySub, IngestionService.LiveSource, [original], OneSnapshot, Token);

        await repository.RecordPullAsync(
            _identitySub, IngestionService.LiveSource, [sparse], OneSnapshot, Token);

        var titleImage = await _database.ScalarAsync<string>(TitleImageSql, Token, _identitySub);
        var keptRaw = await _database.ScalarAsync<string>(keptRawSql, Token, _identitySub);

        Assert.Equal(originalTitleImageUrl.OriginalString, titleImage);
        Assert.Equal(keptPropertyValue, keptRaw);
    }

    [Fact]
    public async Task RecordPullAsync_WithNoSnapshots_StillRecordsThePullAndWritesNoRows()
    {
        var repository = new EntitlementPullRepository(_database.DataSource);

        var pullId = await repository.RecordPullAsync(
            _identitySub, IngestionService.LiveSource, [], NoSnapshots, Token);

        var entryCount = await _database.ScalarAsync<int>(EntryCountSql, Token, pullId);
        var rowCount = await _database.ScalarAsync<long>(SnapshotCountSql, Token, _identitySub);

        Assert.Equal(NoSnapshots, entryCount);
        Assert.Equal(NoSnapshots, rowCount);
    }
}
