namespace Functions.Tests.Integration;

using System.Text.Json.Nodes;
using Functions.Curator.Library;
using TestSupport;

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
        // Arrange
        var activeDate = TestValues.NewUtcTimestampAtSecondPrecision();
        var entitlementId = TestValues.NewEntitlementId();
        var nestedBlockName = TestValues.NewJsonPropertyName();
        var nestedPropertyName = TestValues.NewJsonPropertyName();
        var nestedPropertyValue = TestValues.LowercaseToken(8);
        var snapshot = new EntitlementSnapshot(entitlementId)
        {
            ConceptId = TestValues.NewConceptId(),
            ProductId = TestValues.NewProductId(),
            SkuId = TestValues.NewSkuId(),
            TitleId = TestValues.NewTitleId(),
            GameMetaName = TestValues.NewGameName(),
            ConceptMetaName = TestValues.NewGameName(),
            TitleMetaName = TestValues.NewGameName(),
            PackageType = TestValues.NewPackageType(),
            Active = true,
            ActiveDate = activeDate,
            TitleImageUrl = TestValues.NewCoverImageUri(),
            GameIconUrl = TestValues.NewCoverImageUri(),
            ConceptIconUrl = TestValues.NewCoverImageUri(),
            IsGame = true,
            PlatformIds = [TestValues.NewPlatformId(), TestValues.NewPlatformId()],
            Raw = new JsonObject
            {
                [nestedBlockName] = new JsonObject { [nestedPropertyName] = nestedPropertyValue },
            }.ToJsonString(),
        };
        var repository = new EntitlementPullRepository(_database.DataSource);
        var nestedRawSql =
            $"SELECT {EntitlementSnapshotColumns.Raw} -> '{nestedBlockName}' ->> '{nestedPropertyName}' {SnapshotsForIdentitySql}";

        // Act
        var pullId = await repository.RecordPullAsync(
            _identitySub.ToString(), IngestionService.LiveSource, [snapshot], OneSnapshot, Token);

        // Assert
        var storedEntitlementId = await _database.ScalarAsync<string>(EntitlementIdSql, Token, _identitySub);
        var storedPullId = await _database.ScalarAsync<Guid>(PullIdSql, Token, _identitySub);
        var storedActiveDate = await _database.ScalarAsync<DateTime>(ActiveDateSql, Token, _identitySub);
        var storedPlatformIds = await _database.ScalarAsync<string[]>(PlatformIdsSql, Token, _identitySub);
        var storedNestedRaw = await _database.ScalarAsync<string>(nestedRawSql, Token, _identitySub);

        Assert.Equal(entitlementId, storedEntitlementId);
        Assert.Equal(Guid.Parse(pullId), storedPullId);
        Assert.Equal(activeDate.UtcDateTime, storedActiveDate);
        Assert.Equal(snapshot.PlatformIds, storedPlatformIds);
        Assert.Equal(nestedPropertyValue, storedNestedRaw);
    }

    [Fact]
    public async Task RecordPullAsync_RepullingTheSameEntitlement_UpdatesTheRowRatherThanInsertingASecond()
    {
        // Arrange
        var entitlementId = TestValues.NewEntitlementId();
        var repository = new EntitlementPullRepository(_database.DataSource);
        var repulledPackageType = TestValues.NewPackageType();
        var first = new EntitlementSnapshot(entitlementId)
        {
            PackageType = TestValues.NewPackageType(),
            PlatformIds = [TestValues.NewPlatformId()],
        };
        var second = new EntitlementSnapshot(entitlementId)
        {
            PackageType = repulledPackageType,
            PlatformIds = [TestValues.NewPlatformId()],
        };
        await repository.RecordPullAsync(
            _identitySub.ToString(), IngestionService.LiveSource, [first], OneSnapshot, Token);

        // Act
        await repository.RecordPullAsync(
            _identitySub.ToString(), IngestionService.LiveSource, [second], OneSnapshot, Token);

        // Assert
        var rowCount = await _database.ScalarAsync<long>(SnapshotCountSql, Token, _identitySub);
        var packageType = await _database.ScalarAsync<string>(PackageTypeSql, Token, _identitySub);

        Assert.Equal(OneSnapshot, rowCount);
        Assert.Equal(repulledPackageType, packageType);
    }

    [Fact]
    public async Task RecordPullAsync_WhenARepullCarriesNoRawOrArtwork_KeepsWhatTheEarlierPullStored()
    {
        // Arrange
        var entitlementId = TestValues.NewEntitlementId();
        var repository = new EntitlementPullRepository(_database.DataSource);
        var originalTitleImageUrl = TestValues.NewCoverImageUri();
        var keptPropertyName = TestValues.NewJsonPropertyName();
        var keptPropertyValue = TestValues.LowercaseToken(8);
        var platformIds = new[] { TestValues.NewPlatformId() };
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
            _identitySub.ToString(), IngestionService.LiveSource, [original], OneSnapshot, Token);

        // Act
        await repository.RecordPullAsync(
            _identitySub.ToString(), IngestionService.LiveSource, [sparse], OneSnapshot, Token);

        // Assert
        var titleImage = await _database.ScalarAsync<string>(TitleImageSql, Token, _identitySub);
        var keptRaw = await _database.ScalarAsync<string>(keptRawSql, Token, _identitySub);

        Assert.Equal(originalTitleImageUrl.OriginalString, titleImage);
        Assert.Equal(keptPropertyValue, keptRaw);
    }

    [Fact]
    public async Task RecordPullAsync_WithNoSnapshots_StillRecordsThePullAndWritesNoRows()
    {
        // Arrange
        var repository = new EntitlementPullRepository(_database.DataSource);

        // Act
        var pullId = await repository.RecordPullAsync(
            _identitySub.ToString(), IngestionService.LiveSource, [], NoSnapshots, Token);

        // Assert
        var entryCount = await _database.ScalarAsync<int>(EntryCountSql, Token, Guid.Parse(pullId));
        var rowCount = await _database.ScalarAsync<long>(SnapshotCountSql, Token, _identitySub);

        Assert.Equal(NoSnapshots, entryCount);
        Assert.Equal(NoSnapshots, rowCount);
    }
}
