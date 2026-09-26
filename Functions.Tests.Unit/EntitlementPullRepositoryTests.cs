namespace Functions.Tests.Unit;

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Functions.Curator.Library;
using Functions.Curator.Psn;
using Functions.Tests.Unit.TestSupport;
using static Functions.Tests.Unit.EntitlementPullRepositoryFixtureConstants;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class EntitlementPullRepositoryTests
{
    private static readonly Guid IdentitySub = Guid.NewGuid();
    private static readonly Guid PullId = Guid.NewGuid();

    [Fact]
    public async Task RecordPullAsync_ReturnsTheNewPullId()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        var pullId = await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [Snapshot(NewEntitlementId())],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PullId, pullId);
    }

    [Fact]
    public async Task RecordPullAsync_ThrowsWhenPostgresReturnsNoPullId()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(DBNull.Value));
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        var exception = await Record.ExceptionAsync(() => repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [Snapshot(NewEntitlementId())],
            OneSnapshot,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task RecordPullAsync_StampsThePullRowWithTheSourceAndTheNumberOfEntriesCaptured()
    {
        // Arrange
        var snapshotCount = Random.Shared.Next(2, 5);
        var entitlementIds = NewEntitlementIds(snapshotCount);
        var dataSource = SeededDataSource(snapshotCount: entitlementIds.Count);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [.. entitlementIds.Select(Snapshot)],
            entitlementIds.Count,
            TestContext.Current.CancellationToken);

        // Assert
        var pullCommand = dataSource.ExecutedCommands[0];
        Assert.Contains("INSERT INTO entitlement_pulls", pullCommand.CapturedCommandText, StringComparison.Ordinal);
        Assert.Contains("RETURNING pull_id", pullCommand.CapturedCommandText, StringComparison.Ordinal);
        Assert.Equal(IngestionService.LiveSource, ParamValue(pullCommand, EntitlementPullRepository.SourceParameter));
        Assert.Equal(entitlementIds.Count, ParamValue(pullCommand, EntitlementPullRepository.EntryCountParameter));
        Assert.Equal(IdentitySub, ParamValue(pullCommand, EntitlementPullRepository.IdentitySubParameter));
    }

    [Fact]
    public async Task RecordPullAsync_RecordsAPullRow_WhenTheUserOwnsNothing()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: NoSnapshots);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [],
            NoSnapshots,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OneConnection, dataSource.ConnectionsCreated);
        var pullCommand = Assert.Single(dataSource.ExecutedCommands);
        Assert.Contains("INSERT INTO entitlement_pulls", pullCommand.CapturedCommandText, StringComparison.Ordinal);
        Assert.Equal(0, ParamValue(pullCommand, EntitlementPullRepository.EntryCountParameter));
    }

    [Fact]
    public async Task RecordPullAsync_UpsertsOnIdentitySubAndEntitlementIdRatherThanInsertingOneRowPerPull()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [Snapshot(NewEntitlementId())],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[1].ExecutedSql;
        Assert.Contains("INSERT INTO entitlement_snapshots", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (identity_sub, entitlement_id) DO UPDATE SET", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordPullAsync_KeepsTheStoredArtwork_WhenALaterPullOmitsIt()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [Snapshot(NewEntitlementId())],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[1].ExecutedSql;
        Assert.Contains("title_image_url = COALESCE(EXCLUDED.title_image_url, entitlement_snapshots.title_image_url)", sql, StringComparison.Ordinal);
        Assert.Contains("game_icon_url = COALESCE(EXCLUDED.game_icon_url, entitlement_snapshots.game_icon_url)", sql, StringComparison.Ordinal);
        Assert.Contains("concept_icon_url = COALESCE(EXCLUDED.concept_icon_url, entitlement_snapshots.concept_icon_url)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordPullAsync_KeepsTheStoredRawPayload_WhenTheIncomingOneIsAnEmptyJsonObject()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [Snapshot(NewEntitlementId())],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[1].ExecutedSql;
        Assert.Contains("raw = COALESCE(NULLIF(EXCLUDED.raw, '{}'::jsonb), entitlement_snapshots.raw)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordPullAsync_SetsFirstSeenAtOnInsertOnlyAndLastSeenAtOnBothPaths()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [Snapshot(NewEntitlementId())],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[1].ExecutedSql;
        var conflictStart = sql.IndexOf(ConflictUpdateClause, StringComparison.Ordinal);
        var conflictClauseSql = sql[conflictStart..];
        Assert.Contains("first_seen_at, last_seen_at", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("first_seen_at =", conflictClauseSql, StringComparison.Ordinal);
        Assert.Contains("last_seen_at = now()", conflictClauseSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordPullAsync_CastsTheParametersPostgresCannotInferFromTheirTextRepresentation()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [Snapshot(NewEntitlementId())],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[1].ExecutedSql;
        Assert.Contains("jsonb_to_recordset(@batch::jsonb)", sql, StringComparison.Ordinal);
        Assert.Contains("active_date timestamptz", sql, StringComparison.Ordinal);
        Assert.Contains("platform_ids text[]", sql, StringComparison.Ordinal);
        Assert.Contains("raw jsonb", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordPullAsync_SendsEveryExtractedColumnAlongsideTheRawPayload()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);
        var entitlementId = NewEntitlementId();
        var conceptId = NewConceptId();
        var skuId = Generated.NewSkuId();
        var packageType = Generated.NewPackageType();
        var activeDate = Generated.NewUtcTimestamp();
        var platformIds = NewPlatformIds();
        var rawIdPropertyName = Generated.NewJsonPropertyName();
        var snapshot = new EntitlementSnapshot(entitlementId)
        {
            ConceptId = conceptId,
            ProductId = NewProductId(),
            TitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix),
            GameMetaName = NewGameName(),
            ConceptMetaName = NewGameName(),
            TitleMetaName = NewGameName(),
            PackageType = packageType,
            Active = true,
            SkuId = skuId,
            ActiveDate = activeDate,
            TitleImageUrl = Generated.NewCoverImageUri(),
            GameIconUrl = Generated.NewCoverImageUri(),
            ConceptIconUrl = Generated.NewCoverImageUri(),
            IsGame = true,
            PlatformIds = platformIds,
            Raw = new JsonObject { [rawIdPropertyName] = entitlementId }.ToJsonString(),
        };

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [snapshot],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        var command = dataSource.ExecutedCommands[1];
        Assert.Equal(PullId, ParamValue(command, EntitlementPullRepository.PullIdParameter));
        var row = Assert.Single(BatchRows(command));
        Assert.Equal(entitlementId, row.GetProperty(EntitlementSnapshotColumns.EntitlementId).GetString());
        Assert.Equal(conceptId, row.GetProperty(EntitlementSnapshotColumns.ConceptId).GetString());
        Assert.Equal(skuId, row.GetProperty(EntitlementSnapshotColumns.SkuId).GetString());
        Assert.Equal(activeDate, row.GetProperty(EntitlementSnapshotColumns.ActiveDate).GetDateTimeOffset());
        Assert.Equal(packageType, row.GetProperty(EntitlementSnapshotColumns.PackageType).GetString());
        Assert.True(row.GetProperty(EntitlementSnapshotColumns.IsGame).GetBoolean());
        Assert.Equal(
            platformIds,
            row.GetProperty(EntitlementSnapshotColumns.PlatformIds).EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(
            entitlementId,
            row.GetProperty(EntitlementSnapshotColumns.Raw).GetProperty(rawIdPropertyName).GetString());
    }

    [Fact]
    public async Task RecordPullAsync_SendsNullForEveryColumnPsnLeftOutRatherThanAnEmptyString()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [Snapshot(NewEntitlementId())],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        var row = Assert.Single(BatchRows(dataSource.ExecutedCommands[1]));
        Assert.Equal(
            [JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null],
            new[]
            {
                EntitlementSnapshotColumns.TitleImageUrl,
                EntitlementSnapshotColumns.GameIconUrl,
                EntitlementSnapshotColumns.ConceptIconUrl,
                EntitlementSnapshotColumns.ActiveDate,
                EntitlementSnapshotColumns.Active,
            }.Select(column => row.GetProperty(column).ValueKind));
    }

    [Fact]
    public async Task RecordPullAsync_SendsAnEmptyJsonObject_WhenTheSnapshotCarriesNoRawPayload()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneSnapshot);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [new EntitlementSnapshot(NewEntitlementId()) { Raw = Generated.NewBlankRun() }],
            OneSnapshot,
            TestContext.Current.CancellationToken);

        // Assert
        var row = Assert.Single(BatchRows(dataSource.ExecutedCommands[1]));
        var raw = row.GetProperty(EntitlementSnapshotColumns.Raw);
        Assert.Equal(JsonValueKind.Object, raw.ValueKind);
        Assert.Empty(raw.EnumerateObject());
    }

    [Fact]
    public async Task RecordPullAsync_WritesThePullRowAndEverySnapshotInOneCommittedTransaction()
    {
        // Arrange
        var snapshotCount = Random.Shared.Next(2, 5);
        var entitlementIds = NewEntitlementIds(snapshotCount);
        var dataSource = SeededDataSource(snapshotCount: entitlementIds.Count);
        var repository = new EntitlementPullRepository(dataSource);

        // Act
        await repository.RecordPullAsync(
            IdentitySub,
            IngestionService.LiveSource,
            [.. entitlementIds.Select(Snapshot)],
            entitlementIds.Count,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OneConnection, dataSource.ConnectionsCreated);
        Assert.Equal(PullRowThenSnapshotBatch, dataSource.ExecutedCommands.Count);
        Assert.Equal(
            entitlementIds,
            BatchRows(dataSource.ExecutedCommands[1])
                .Select(row => row.GetProperty(EntitlementSnapshotColumns.EntitlementId).GetString()));
        var transaction = Assert.IsType<FakeDbTransaction>(dataSource.ExecutedCommands[0].Transaction);
        Assert.All(dataSource.ExecutedCommands, command => Assert.Same(transaction, command.Transaction));
        Assert.Equal(OneCommit, transaction.CommitCount);
    }

    private static EntitlementSnapshot Snapshot(string entitlementId) => new(entitlementId);

    private static IReadOnlyList<string> NewEntitlementIds(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => NewEntitlementId())];

    private static IReadOnlyList<string> NewPlatformIds() =>
        [Generated.NewPlatformId(), Generated.NewPlatformId()];

    private static FakeDbDataSource SeededDataSource(int snapshotCount)
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(PullId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(snapshotCount));

        return dataSource;
    }

    private static IReadOnlyList<JsonElement> BatchRows(DbCommand command)
    {
        var batch = Assert.IsType<string>(ParamValue(command, EntitlementPullRepository.BatchParameter));
        return [.. JsonDocument.Parse(batch).RootElement.EnumerateArray()];
    }

    private static object? ParamValue(DbCommand command, string name) =>
        command.Parameters[command.Parameters.IndexOf(name)].Value;
}
