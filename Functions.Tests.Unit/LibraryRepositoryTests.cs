namespace Functions.Tests.Unit;

using System.Data;
using System.Text.Json;
using Curator.Library;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class LibraryRepositoryTests
{
    private static readonly string IdentitySub = Guid.NewGuid().ToString();
    private static readonly string GameId = Guid.NewGuid().ToString();
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
        Assert.Contains(
            "DELETE FROM library_entry_platforms",
            dataSource.ExecutedCommands[1].CapturedCommandText,
            StringComparison.Ordinal);
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
        Assert.Equal(3, dataSource.ExecutedCommands.Count);
        Assert.Contains(
            "INSERT INTO library_entry_platforms",
            dataSource.ExecutedCommands[2].CapturedCommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertEntryAsync_OrdersPlatformsPs5ThenPs4ThenTheExtras()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(repository, nativePs5: true, ps4Eligible: true, platforms: ["PS3", "PSVITA"]);

        // Assert
        Assert.Equal(["PS5", "PS4", "PS3", "PSVITA"], OwnedPlatforms(dataSource));
    }

    [Fact]
    public async Task UpsertEntryAsync_DoesNotRepeatAPlatformAlreadyImpliedByTheBooleanPair()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(repository, nativePs5: true, ps4Eligible: true, platforms: ["PS4"]);

        // Assert
        Assert.Equal(["PS5", "PS4"], OwnedPlatforms(dataSource));
    }

    [Fact]
    public async Task UpsertEntryAsync_DeduplicatesRepeatsWithinTheSuppliedPlatformsThemselves()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);

        // Act
        await UpsertAsync(repository, platforms: ["PS3", "PS3", "PSP"]);

        // Assert
        Assert.Equal(["PS3", "PSP"], OwnedPlatforms(dataSource));
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
        dataSource.Enqueue(FakeDbCommand.WithReader(ContinuationTable()));
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
        dataSource.Enqueue(FakeDbCommand.WithReader(ContinuationTable()));
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
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(
            ContinuationTable(gameId, title, productId, titleId, true)));
        var repository = new LibraryRepository(dataSource);

        // Act
        var games = await repository.GetGamesForContinuationAsync(
            IdentitySub, [gameId.ToString()], TestContext.Current.CancellationToken);

        // Assert
        var game = Assert.Single(games);
        Assert.Equal(gameId.ToString(), game.GameId);
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
            IdentitySub, [gameId.ToString()], TestContext.Current.CancellationToken);

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
        Assert.Contains(
            "trophy_progress_fetched_at = CASE WHEN @percent_completed::smallint IS NULL",
            sql,
            StringComparison.Ordinal);
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
        var firstPercent = NewTrophyPercent();
        var secondPercent = NewTrophyPercent();
        var rowsUpdated = 2;
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
        var batch = dataSource.ExecutedCommands[0].ParameterValue<string>("@batch");
        Assert.Equal(
            [(firstTrophyTitle, firstPercent), (secondTrophyTitle, secondPercent)],
            JsonDocument.Parse(batch).RootElement.EnumerateArray().Select(row =>
                (row.GetProperty("np_communication_id").GetString(), row.GetProperty("percent").GetInt32())));
    }

    [Fact]
    public async Task RefreshTrophyProgressAsync_UsesOneConnectionForTheWholeBatch()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new LibraryRepository(dataSource);
        var progress = new Dictionary<string, int>
        {
            [NewNpCommunicationId()] = NewTrophyPercent(),
            [NewNpCommunicationId()] = NewTrophyPercent(),
        };

        // Act
        await repository.RefreshTrophyProgressAsync(IdentitySub, progress, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, dataSource.ConnectionsCreated);
    }

    private static string NewGameTitle() => $"Game {Guid.NewGuid():N}";

    private static string NewProductId() => TestValues.NewProductId();

    private static string NewTitleId() =>
        $"{TrophyMatchService.Ps4TitleIdPrefix}{Random.Shared.Next(10_000, 99_999)}_00";

    private static string NewNpCommunicationId() => TestValues.NewNpCommunicationId();

    private static int NewTrophyPercent() => Random.Shared.Next(1, 100);

    private static DataTable ContinuationTable(
        Guid? gameId = null,
        string title = "Game",
        string? productId = "prod-1",
        string? titleId = "CUSA00011_00",
        bool nativePs5 = false)
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("product_id", typeof(string));
        table.Columns.Add("title_id", typeof(string));
        table.Columns.Add("native_ps5", typeof(bool));
        if (gameId is { } id)
        {
            table.Rows.Add(id, title, (object?)productId ?? DBNull.Value, (object?)titleId ?? DBNull.Value, nativePs5);
        }

        return table;
    }

    private static IReadOnlyList<string?> OwnedPlatforms(FakeDbDataSource dataSource)
    {
        var batch = dataSource.ExecutedCommands[0].ParameterValue<string>("@batch");
        var row = JsonDocument.Parse(batch).RootElement[0];
        return [.. row.GetProperty("platforms").EnumerateArray().Select(platform => platform.GetString())];
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
