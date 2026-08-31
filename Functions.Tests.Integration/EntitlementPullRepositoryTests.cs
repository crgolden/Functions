namespace Functions.Tests.Integration;

using Functions.Curator.Library;
using TestSupport;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class EntitlementPullRepositoryTests : IAsyncLifetime
{
    private const string EntitlementIdSql =
        "SELECT entitlement_id FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string PullIdSql =
        "SELECT pull_id FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string ActiveDateSql =
        "SELECT active_date FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string PlatformIdsSql =
        "SELECT platform_ids FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string NestedRawSql =
        "SELECT raw -> 'nested' ->> 'kept' FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string KeptRawSql =
        "SELECT raw ->> 'kept' FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string PackageTypeSql =
        "SELECT package_type FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string TitleImageSql =
        "SELECT title_image_url FROM entitlement_snapshots WHERE identity_sub = $1";

    private const string SnapshotCountSql =
        "SELECT count(*) FROM entitlement_snapshots WHERE identity_sub = $1";

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
        var activeDate = new DateTimeOffset(2026, 3, 14, 15, 9, 26, TimeSpan.Zero);
        var entitlementId = NewEntitlementId();
        var snapshot = new EntitlementSnapshot(entitlementId)
        {
            ConceptId = "10000123",
            ProductId = "PROD-A",
            SkuId = "SKU-A",
            TitleId = "CUSA00001_00",
            GameMetaName = "Game Meta Name",
            ConceptMetaName = "Concept Meta Name",
            TitleMetaName = "Title Meta Name",
            PackageType = "PS5GD",
            Active = true,
            ActiveDate = activeDate,
            TitleImageUrl = "https://example.invalid/title.png",
            GameIconUrl = "https://example.invalid/game.png",
            ConceptIconUrl = "https://example.invalid/concept.png",
            IsGame = true,
            PlatformIds = ["PS5", "PS4"],
            Raw = """{"entitlementId":"seeded","nested":{"kept":"yes"}}""",
        };
        var repository = new EntitlementPullRepository(_database.DataSource);

        // Act
        var pullId = await repository.RecordPullAsync(_identitySub.ToString(), IngestionService.LiveSource, [snapshot], 1, Token);

        // Assert
        var storedEntitlementId = await _database.ScalarAsync<string>(EntitlementIdSql, Token, _identitySub);
        var storedPullId = await _database.ScalarAsync<Guid>(PullIdSql, Token, _identitySub);
        var storedActiveDate = await _database.ScalarAsync<DateTime>(ActiveDateSql, Token, _identitySub);
        var storedPlatformIds = await _database.ScalarAsync<string[]>(PlatformIdsSql, Token, _identitySub);
        var storedNestedRaw = await _database.ScalarAsync<string>(NestedRawSql, Token, _identitySub);

        Assert.Equal(entitlementId, storedEntitlementId);
        Assert.Equal(Guid.Parse(pullId), storedPullId);
        Assert.Equal(activeDate.UtcDateTime, storedActiveDate);
        Assert.Equal(snapshot.PlatformIds, storedPlatformIds);
        Assert.Equal("yes", storedNestedRaw);
    }

    [Fact]
    public async Task RecordPullAsync_RepullingTheSameEntitlement_UpdatesTheRowRatherThanInsertingASecond()
    {
        // Arrange
        var entitlementId = NewEntitlementId();
        var repository = new EntitlementPullRepository(_database.DataSource);
        var first = new EntitlementSnapshot(entitlementId) { PackageType = "PS4GD", PlatformIds = ["PS4"] };
        var second = new EntitlementSnapshot(entitlementId) { PackageType = "PS5GD", PlatformIds = ["PS5"] };
        await repository.RecordPullAsync(_identitySub.ToString(), IngestionService.LiveSource, [first], 1, Token);

        // Act
        await repository.RecordPullAsync(_identitySub.ToString(), IngestionService.LiveSource, [second], 1, Token);

        // Assert
        var rowCount = await _database.ScalarAsync<long>(SnapshotCountSql, Token, _identitySub);
        var packageType = await _database.ScalarAsync<string>(PackageTypeSql, Token, _identitySub);

        Assert.Equal(1L, rowCount);
        Assert.Equal("PS5GD", packageType);
    }

    [Fact]
    public async Task RecordPullAsync_WhenARepullCarriesNoRawOrArtwork_KeepsWhatTheEarlierPullStored()
    {
        // Arrange
        var entitlementId = NewEntitlementId();
        var repository = new EntitlementPullRepository(_database.DataSource);
        var original = new EntitlementSnapshot(entitlementId)
        {
            TitleImageUrl = "https://example.invalid/original.png",
            PlatformIds = ["PS5"],
            Raw = """{"kept":"original"}""",
        };
        var sparse = new EntitlementSnapshot(entitlementId) { PlatformIds = ["PS5"] };
        await repository.RecordPullAsync(_identitySub.ToString(), IngestionService.LiveSource, [original], 1, Token);

        // Act
        await repository.RecordPullAsync(_identitySub.ToString(), IngestionService.LiveSource, [sparse], 1, Token);

        // Assert
        var titleImage = await _database.ScalarAsync<string>(TitleImageSql, Token, _identitySub);
        var keptRaw = await _database.ScalarAsync<string>(KeptRawSql, Token, _identitySub);

        Assert.Equal("https://example.invalid/original.png", titleImage);
        Assert.Equal("original", keptRaw);
    }

    [Fact]
    public async Task RecordPullAsync_WithNoSnapshots_StillRecordsThePullAndWritesNoRows()
    {
        // Arrange
        var repository = new EntitlementPullRepository(_database.DataSource);

        // Act
        var pullId = await repository.RecordPullAsync(_identitySub.ToString(), IngestionService.LiveSource, [], 0, Token);

        // Assert
        var entryCount = await _database.ScalarAsync<int>(EntryCountSql, Token, Guid.Parse(pullId));
        var rowCount = await _database.ScalarAsync<long>(SnapshotCountSql, Token, _identitySub);

        Assert.Equal(0, entryCount);
        Assert.Equal(0L, rowCount);
    }

    private static string NewEntitlementId() => TestValues.NewEntitlementId();
}
