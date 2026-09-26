namespace Functions.Tests.Unit;

using System.Data;
using System.Text.Json;
using Functions.Curator;
using Functions.Curator.Library;
using Functions.Curator.Psn;
using Functions.Tests.Unit.TestSupport;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class LibraryRepositoryTests
{
    private static readonly Guid IdentitySub = Guid.NewGuid();
    private static readonly Guid GameId = Guid.NewGuid();
    private static readonly string WinningEntitlementId = Guid.NewGuid().ToString();

    [Fact]
    public async Task UpsertEntryAsync_UpsertsOnTheUserAndGamePair()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(repository);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("ON CONFLICT (identity_sub, game_id) DO UPDATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertDownloadSizesAsync_ResolvesEachSizeThroughTheCallersOwnLibraryEntryAndKeepsTheLargestPerPlatform()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new LibraryRepository(dataSource);
        var size = new EntitlementDownloadSize(
            Generated.NewEntitlementId(), Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), Generated.NewPlatformId(), Generated.NewDownloadSizeBytes());

        // Act
        var written = await repository.UpsertDownloadSizesAsync(IdentitySub, [size], TestContext.Current.CancellationToken);

        // Assert
        var command = dataSource.ExecutedCommands[0];
        Assert.Equal(1, written);
        Assert.Equal(IdentitySub, command.Parameters[CuratorSqlParameters.IdentitySub].Value);
        Assert.Contains("INSERT INTO game_download_sizes", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Contains("JOIN library_entries le ON le.identity_sub = @identity_sub AND le.title_id = s.title_id", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Contains("DISTINCT ON (le.game_id, s.platform)", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY le.game_id, s.platform, s.bytes DESC", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (game_id, platform) DO UPDATE", command.ExecutedSql, StringComparison.Ordinal);
        var batch = Assert.IsType<string>(command.Parameters[CuratorSqlParameters.Batch].Value);
        var row = Assert.Single(Assert.IsType<EntitlementDownloadSize[]>(JsonSerializer.Deserialize<EntitlementDownloadSize[]>(batch, LibraryRepository.BatchFormat)));
        Assert.Equal(size, row);
    }

    [Fact]
    public async Task UpsertDownloadSizesAsync_OpensNoConnection_WhenThereIsNothingToWrite()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        var written = await repository.UpsertDownloadSizesAsync(IdentitySub, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, written);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task UpsertEntryAsync_WritesTheEntryAndItsPlatformsOnOneConnection()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(repository, nativePs5: true);

        // Assert
        Assert.Equal(1, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task UpsertEntryAsync_DeletesEveryPlatformRow_WhenTheEntryOwnsNoPlatformAtAll()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(repository, nativePs5: false, ps4Eligible: false);

        // Assert
        Assert.Contains("DELETE FROM library_entry_platforms", dataSource.ExecutedCommands[1].ExecutedSql, StringComparison.Ordinal);
        Assert.Empty(OwnedPlatforms(dataSource));
    }

    [Fact]
    public async Task UpsertEntryAsync_InsertsTheOwnedPlatforms_WhenTheEntryOwnsSome()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(repository, nativePs5: true);

        // Assert
        Assert.Collection(
            dataSource.ExecutedCommands,
            upsertEntriesSql => Assert.Contains("INSERT INTO library_entries", upsertEntriesSql.ExecutedSql, StringComparison.Ordinal),
            deletePlatformsSql => Assert.Contains("DELETE FROM library_entry_platforms", deletePlatformsSql.ExecutedSql, StringComparison.Ordinal),
            insertPlatformsSql => Assert.Contains("INSERT INTO library_entry_platforms", insertPlatformsSql.ExecutedSql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpsertEntryAsync_OrdersPlatformsPs5ThenPs4ThenTheExtras()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(
            repository,
            nativePs5: true,
            ps4Eligible: true,
            platforms: [TitlePlatform.Ps3, TitlePlatform.PsVita]);

        // Assert
        Assert.Equal(
            [TitlePlatform.Ps5, TitlePlatform.Ps4, TitlePlatform.Ps3, TitlePlatform.PsVita],
            OwnedPlatforms(dataSource));
    }

    [Fact]
    public async Task UpsertEntryAsync_DoesNotRepeatAPlatformAlreadyImpliedByTheBooleanPair()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(repository, nativePs5: true, ps4Eligible: true, platforms: [TitlePlatform.Ps4]);

        // Assert
        Assert.Equal([TitlePlatform.Ps5, TitlePlatform.Ps4], OwnedPlatforms(dataSource));
    }

    [Fact]
    public async Task UpsertEntryAsync_DeduplicatesRepeatsWithinTheSuppliedPlatformsThemselves()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(
            repository,
            platforms: [TitlePlatform.Ps3, TitlePlatform.Ps3, TitlePlatform.Psp]);

        // Assert
        Assert.Equal([TitlePlatform.Ps3, TitlePlatform.Psp], OwnedPlatforms(dataSource));
    }

    [Fact]
    public async Task GetUnmatchedGameIdsAsync_QueriesNothing_WhenGivenNoCandidates()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        var unmatched = await repository.GetUnmatchedGameIdsAsync(
            IdentitySub, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(unmatched);
        Assert.Empty(dataSource.ExecutedCommands);
    }

    [Fact]
    public async Task GetUnmatchedGameIdsAsync_SelectsOnlyEntriesWithNoPersistedTrophyMatch()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await repository.GetUnmatchedGameIdsAsync(IdentitySub, [GameId], TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("np_communication_id IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGamesForContinuationAsync_QueriesNothing_WhenThereAreNoGamesToResume()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        var games = await repository.GetGamesForContinuationAsync(
            IdentitySub, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(games);
        Assert.Empty(dataSource.ExecutedCommands);
    }

    [Fact]
    public async Task GetGamesForContinuationAsync_ScopesTheLookupToTheResumingUser()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(EmptyContinuationTable()));
        var repository = new LibraryRepository(dataSource);

        // Act
        await repository.GetGamesForContinuationAsync(
            IdentitySub, [GameId], TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("le.identity_sub = @identity_sub", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGamesForContinuationAsync_ReadsTheTitleFromTheSharedCatalogRatherThanTheEntry()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(EmptyContinuationTable()));
        var repository = new LibraryRepository(dataSource);

        // Act
        await repository.GetGamesForContinuationAsync(
            IdentitySub, [GameId], TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("JOIN games g ON g.game_id = le.game_id", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGamesForContinuationAsync_MapsEveryFieldEnrichmentNeedsToResume()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var title = NewGameTitle();
        var productId = NewProductId();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(
            ContinuationTable(gameId, title, productId, titleId, true)));
        var repository = new LibraryRepository(dataSource);

        // Act
        var games = await repository.GetGamesForContinuationAsync(
            IdentitySub, [gameId], TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(games);
        Assert.Equal(gameId, game.GameId);
        Assert.Equal(title, game.Title);
        Assert.Equal(productId, game.ProductId);
        Assert.Equal(titleId, game.TitleId);
        Assert.True(game.NativePs5);
    }

    [Fact]
    public async Task GetGamesForContinuationAsync_ReadsAGamePsnGaveNoProductOrTitleId()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(ContinuationTable(gameId, NewGameTitle(), null, null)));
        var repository = new LibraryRepository(dataSource);

        // Act
        var games = await repository.GetGamesForContinuationAsync(
            IdentitySub, [gameId], TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(games);
        Assert.Null(game.ProductId);
        Assert.Null(game.TitleId);
    }

    [Fact]
    public async Task SetTrophyMatchAsync_StampsTheAttempt_EvenWhenNothingMatched()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await repository.SetTrophyMatchAsync(
            IdentitySub,
            GameId,
            npCommunicationId: null,
            method: null,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("trophy_match_attempted_at = now()", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetTrophyMatchAsync_LeavesTheProgressTimestampAlone_WhenNoPercentageIsSupplied()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await repository.SetTrophyMatchAsync(
            IdentitySub,
            GameId,
            npCommunicationId: null,
            method: null,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains("trophy_progress_fetched_at = CASE WHEN @percent_completed::smallint IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshTrophyProgressAsync_QueriesNothing_WhenThereIsNoProgressToWrite()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        var updated = await repository.RefreshTrophyProgressAsync(
            IdentitySub, new Dictionary<string, int>(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, updated);
        Assert.Empty(dataSource.ExecutedCommands);
    }

    [Fact]
    public async Task RefreshTrophyProgressAsync_ReportsTheRowsUpdatedAcrossEveryTrophyTitle()
    {
        // Arrange
        var firstTrophyTitle = NewNpCommunicationId();
        var secondTrophyTitle = NewNpCommunicationId();
        var firstPercent = NewTrophyProgress();
        var secondPercent = NewTrophyProgress();
        var rowsUpdated = Random.Shared.Next(1, 1_000);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(rowsUpdated));
        var repository = new LibraryRepository(dataSource);
        var progress = new Dictionary<string, int>
        {
            [firstTrophyTitle] = firstPercent,
            [secondTrophyTitle] = secondPercent,
        };

        // Act
        var updated = await repository.RefreshTrophyProgressAsync(
            IdentitySub, progress, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rowsUpdated, updated);
        var batch = Assert.IsType<string>(dataSource.ExecutedCommands[0].Parameters[CuratorSqlParameters.Batch].Value);
        var rows = Assert.IsType<TrophyProgressRow[]>(JsonSerializer.Deserialize<TrophyProgressRow[]>(batch));
        Assert.Equal(
            [(firstTrophyTitle, firstPercent), (secondTrophyTitle, secondPercent)],
            rows.Select(row => (row.NpCommunicationId, row.Percent)));
    }

    [Fact]
    public async Task RefreshTrophyProgressAsync_UsesOneConnectionForTheWholeBatch()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);
        var progress = new Dictionary<string, int>
        {
            [NewNpCommunicationId()] = NewTrophyProgress(),
            [NewNpCommunicationId()] = NewTrophyProgress(),
        };

        // Act
        await repository.RefreshTrophyProgressAsync(IdentitySub, progress, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, dataSource.ConnectionsCreated);
    }

    private static DataTable EmptyContinuationTable()
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(bool));
        return table;
    }

    private static DataTable ContinuationTable(
        Guid gameId,
        string title,
        string? productId,
        string? titleId,
        bool nativePs5 = false)
    {
        var table = EmptyContinuationTable();
        table.Rows.Add(gameId, title, (object?)productId ?? DBNull.Value, (object?)titleId ?? DBNull.Value, nativePs5);
        return table;
    }

    private static IReadOnlyList<string> OwnedPlatforms(FakeDbDataSource dataSource)
    {
        var batch = Assert.IsType<string>(dataSource.ExecutedCommands[0].Parameters[CuratorSqlParameters.Batch].Value);
        var entries = Assert.IsType<LibraryEntryRow[]>(JsonSerializer.Deserialize<LibraryEntryRow[]>(batch));
        return Assert.Single(entries).Platforms;
    }

    private static Task UpsertAsync(
        LibraryRepository repository,
        bool nativePs5 = false,
        bool ps4Eligible = false) =>
        UpsertAsync(repository, [], nativePs5, ps4Eligible);

    private static Task UpsertAsync(
        LibraryRepository repository,
        IReadOnlyList<string> platforms,
        bool nativePs5 = false,
        bool ps4Eligible = false) =>
        repository.UpsertEntryAsync(
            IdentitySub,
            GameId,
            nativePs5,
            ps4Eligible,
            ownedEdition: null,
            winningEntitlementId: WinningEntitlementId,
            productId: null,
            titleId: null,
            platforms: platforms,
            cancellationToken: TestContext.Current.CancellationToken);
}
