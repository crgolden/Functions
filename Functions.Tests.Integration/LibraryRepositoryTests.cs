namespace Functions.Tests.Integration;

using Functions.Curator.Library;
using Functions.Curator.Psn;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class LibraryRepositoryTests : IAsyncLifetime
{
    private const string InsertGameSql =
        "INSERT INTO games (game_id, canonical_title, normalized_title) VALUES ($1, $2, $3)";

    private const string PlatformsSql =
        "SELECT array_agg(platform ORDER BY platform) FROM library_entry_platforms WHERE identity_sub = $1 AND game_id = $2";

    private const string TitleIdSql =
        "SELECT title_id FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

    private const string PercentSql =
        "SELECT trophy_percent_completed FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

    private const string ProgressFetchedIsNullSql =
        "SELECT trophy_progress_fetched_at IS NULL FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

    private const string MatchMethodSql =
        "SELECT trophy_match_method FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

    private const string EntryCountSql =
        "SELECT count(*) FROM library_entries WHERE identity_sub = $1";

    private const string SourceSql =
        "SELECT source FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

    private const string EntitlementSql =
        "SELECT winning_entitlement_id FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

    private const string IsActiveSql =
        "SELECT is_active FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

    private const string InsertManualEntrySql =
        $"INSERT INTO library_entries (identity_sub, game_id, source) VALUES ($1, $2, '{LibraryEntrySources.Manual}')";

    private const string DeleteGameSql = "DELETE FROM games WHERE game_id = $1";

    private const string DownloadSizeSql =
        "SELECT bytes FROM game_download_sizes WHERE game_id = $1 AND platform = $2";

    private readonly CuratorDatabase _database;
    private readonly List<Guid> _createdGames = [];
    private Guid _identitySub;

    public LibraryRepositoryTests(CuratorDatabase database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _identitySub = await _database.CreateUserAsync(Token);

    public async ValueTask DisposeAsync()
    {
        await _database.DeleteUserAsync(_identitySub, Token);
        foreach (var gameId in _createdGames)
        {
            await _database.ExecuteAsync(DeleteGameSql, Token, gameId);
        }
    }

    [Fact]
    public async Task UpsertEntryAsync_WithPlatforms_ParsesTheGameIdAsUuidAndFansOutThePlatformArray()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var storedTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var repository = new LibraryRepository(_database.DataSource);

        await repository.UpsertEntryAsync(
            _identitySub,
            gameId,
            nativePs5: true,
            ps4Eligible: true,
            ownedEdition: Generated.NewOwnedEdition(),
            winningEntitlementId: Generated.NewEntitlementId(),
            productId: Generated.NewProductId(),
            titleId: storedTitleId,
            platforms: [TitlePlatform.Ps5, TitlePlatform.Ps4],
            isActive: true,
            cancellationToken: Token);

        var platforms = await _database.ScalarAsync<string[]>(PlatformsSql, Token, _identitySub, gameId);
        var titleId = await _database.ScalarAsync<string>(TitleIdSql, Token, _identitySub, gameId);

        Assert.Equal([TitlePlatform.Ps4, TitlePlatform.Ps5], platforms);
        Assert.Equal(storedTitleId, titleId);
    }

    [Fact]
    public async Task UpsertEntryAsync_WhenARepullDropsAPlatform_DeletesOnlyThePlatformNoLongerOwned()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var ownedEdition = Generated.NewOwnedEdition();
        var entitlementId = Generated.NewEntitlementId();
        var productId = Generated.NewProductId();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var repository = new LibraryRepository(_database.DataSource);
        await repository.UpsertEntryAsync(
            _identitySub,
            gameId,
            nativePs5: true,
            ps4Eligible: true,
            ownedEdition: ownedEdition,
            winningEntitlementId: entitlementId,
            productId: productId,
            titleId: titleId,
            platforms: [TitlePlatform.Ps5, TitlePlatform.Ps4],
            isActive: true,
            cancellationToken: Token);

        await repository.UpsertEntryAsync(
            _identitySub,
            gameId,
            nativePs5: true,
            ps4Eligible: false,
            ownedEdition: ownedEdition,
            winningEntitlementId: entitlementId,
            productId: productId,
            titleId: titleId,
            platforms: [TitlePlatform.Ps5],
            isActive: true,
            cancellationToken: Token);

        var platforms = await _database.ScalarAsync<string[]>(PlatformsSql, Token, _identitySub, gameId);
        var entries = await _database.ScalarAsync<long>(EntryCountSql, Token, _identitySub);

        Assert.Equal([TitlePlatform.Ps5], platforms);
        Assert.Equal(1L, entries);
    }

    [Fact]
    public async Task UpsertEntryAsync_ForAGameAlreadyStored_UpdatesTheRowInPlace()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var repository = new LibraryRepository(_database.DataSource);
        await repository.UpsertEntryAsync(
            _identitySub,
            gameId,
            nativePs5: false,
            ps4Eligible: true,
            ownedEdition: Generated.NewOwnedEdition(),
            winningEntitlementId: Generated.NewEntitlementId(),
            productId: Generated.NewProductId(),
            titleId: titleId,
            platforms: [TitlePlatform.Ps4],
            isActive: true,
            cancellationToken: Token);

        await repository.UpsertEntryAsync(
            _identitySub,
            gameId,
            nativePs5: true,
            ps4Eligible: false,
            ownedEdition: Generated.NewOwnedEdition(),
            winningEntitlementId: Generated.NewEntitlementId(),
            productId: Generated.NewProductId(),
            titleId: titleId,
            platforms: [TitlePlatform.Ps5],
            isActive: true,
            cancellationToken: Token);

        var platforms = await _database.ScalarAsync<string[]>(PlatformsSql, Token, _identitySub, gameId);
        var entries = await _database.ScalarAsync<long>(EntryCountSql, Token, _identitySub);

        Assert.Equal([TitlePlatform.Ps5], platforms);
        Assert.Equal(1L, entries);
    }

    [Fact]
    public async Task GetUnmatchedGameIdsAsync_CastsTheIdArrayAndReturnsOnlyEntriesWithNoTrophyMatch()
    {
        var matched = await CreateGameAsync(Generated.NewGameTitle());
        var unmatched = await CreateGameAsync(Generated.NewGameTitle());
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, matched);
        await UpsertMinimalAsync(repository, unmatched);
        await repository.SetTrophyMatchAsync(
            _identitySub,
            matched,
            Generated.NewNpCommunicationId(),
            TrophyMatchService.ExactMatchMethod,
            null,
            Token);

        var result = await repository.GetUnmatchedGameIdsAsync(
            _identitySub, [matched, unmatched], Token);

        Assert.Equal([unmatched], result);
    }

    [Fact]
    public async Task SetTrophyMatchAsync_WithAPercent_StampsTheProgressFetchedTimestamp()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var trophyPercentComplete = Generated.NewTrophyProgress();
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, gameId);

        await repository.SetTrophyMatchAsync(
            _identitySub,
            gameId,
            Generated.NewNpCommunicationId(),
            TrophyMatchService.FuzzyMatchMethod,
            trophyPercentComplete,
            Token);

        var percent = await _database.ScalarAsync<short>(PercentSql, Token, _identitySub, gameId);
        var method = await _database.ScalarAsync<string>(MatchMethodSql, Token, _identitySub, gameId);
        var fetchedIsNull = await _database.ScalarAsync<bool>(
            ProgressFetchedIsNullSql, Token, _identitySub, gameId);

        Assert.Equal((short)trophyPercentComplete, percent);
        Assert.Equal(TrophyMatchService.FuzzyMatchMethod, method);
        Assert.False(fetchedIsNull);
    }

    [Fact]
    public async Task SetTrophyMatchAsync_WithANullPercent_LeavesTheProgressTimestampAlone()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, gameId);

        await repository.SetTrophyMatchAsync(
            _identitySub,
            gameId,
            Generated.NewNpCommunicationId(),
            TrophyMatchService.ExactMatchMethod,
            null,
            Token);

        var fetchedIsNull = await _database.ScalarAsync<bool>(
            ProgressFetchedIsNullSql, Token, _identitySub, gameId);

        Assert.True(fetchedIsNull);
    }

    [Fact]
    public async Task RefreshTrophyProgressAsync_UpdatesEveryMatchedNpCommunicationIdAndReportsTheCount()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var npCommunicationId = Generated.NewNpCommunicationId();
        var refreshedProgress = Generated.NewTrophyProgress();
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, gameId);
        await repository.SetTrophyMatchAsync(
            _identitySub,
            gameId,
            npCommunicationId,
            TrophyMatchService.ExactMatchMethod,
            null,
            Token);

        var updated = await repository.RefreshTrophyProgressAsync(
            _identitySub,
            new Dictionary<string, int> { [npCommunicationId] = refreshedProgress },
            Token);

        var percent = await _database.ScalarAsync<short>(PercentSql, Token, _identitySub, gameId);

        Assert.Equal(1, updated);
        Assert.Equal((short)refreshedProgress, percent);
    }

    [Fact]
    public async Task GetGamesForContinuationAsync_JoinsGamesAndReturnsTheCanonicalTitle()
    {
        var canonicalTitle = Generated.NewGameTitle();
        var gameId = await CreateGameAsync(canonicalTitle);
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, gameId);

        var games = await repository.GetGamesForContinuationAsync(
            _identitySub, [gameId], Token);

        var only = Assert.Single(games);

        Assert.Equal(gameId, only.GameId);
        Assert.Equal(canonicalTitle, only.Title);
    }

    [Fact]
    public async Task UpsertEntriesAsync_WritesPsnSourcedRowsCarryingTheirEntitlementId()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var sourcedEntitlementId = Generated.NewEntitlementId();
        var repository = new LibraryRepository(_database.DataSource);
        var entries = new List<LibraryEntryRow>
        {
            LibraryEntryRow.Create(gameId, true, false, null, sourcedEntitlementId, null, null, [TitlePlatform.Ps5], true),
        };

        await repository.UpsertEntriesAsync(_identitySub, entries, Token);

        var source = await _database.ScalarAsync<string>(SourceSql, Token, _identitySub, gameId);
        var entitlement = await _database.ScalarAsync<string>(
            EntitlementSql, Token, _identitySub, gameId);

        Assert.Equal(LibraryEntrySources.Psn, source);
        Assert.Equal(sourcedEntitlementId, entitlement);
    }

    [Fact]
    public async Task UpsertEntriesAsync_LeavesAHandAddedRowAlone_WhenPsnReportsTheSameGameAsLapsed()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        await _database.ExecuteAsync(InsertManualEntrySql, Token, _identitySub, gameId);
        var repository = new LibraryRepository(_database.DataSource);
        var lapsed = new List<LibraryEntryRow>
        {
            LibraryEntryRow.Create(
                gameId, true, false, null, Generated.NewEntitlementId(), null, null, [TitlePlatform.Ps5], false),
        };

        await repository.UpsertEntriesAsync(_identitySub, lapsed, Token);

        var source = await _database.ScalarAsync<string>(SourceSql, Token, _identitySub, gameId);
        var isActive = await _database.ScalarAsync<bool>(IsActiveSql, Token, _identitySub, gameId);

        const string reason =
            "A lapsed entitlement must not claim a row the reader added by hand. Without the ON CONFLICT "
            + "guard the upsert rewrites source to 'psn' and is_active to false, and Curator's "
            + "upsert_manual_entry then refuses the row because it only touches source = 'manual' - so the "
            + "entry is destroyed and cannot be re-added.";

        Assert.Equal(LibraryEntrySources.Manual, source);
        Assert.True(isActive, reason);
    }

    [Fact]
    public async Task UpsertEntriesAsync_TwoCanonicalGamesResolvingToOneGameId_MergesBothPlatformsOntoOneRow()
    {
        var duplicatedGameTitle = Guid.NewGuid().ToString();
        var gameId = await CreateGameAsync(duplicatedGameTitle);
        var repository = new LibraryRepository(_database.DataSource);
        var supersededEntitlementId = Guid.NewGuid().ToString();
        var winningEntitlementId = Guid.NewGuid().ToString();
        var entries = new List<LibraryEntryRow>
        {
            LibraryEntryRow.Create(gameId, false, true, null, supersededEntitlementId, null, null, [TitlePlatform.Ps4], true),
            LibraryEntryRow.Create(gameId, true, false, null, winningEntitlementId, null, null, [TitlePlatform.Ps5], true),
        };

        await repository.UpsertEntriesAsync(_identitySub, entries, Token);

        var rowCount = await _database.ScalarAsync<long>(EntryCountSql, Token, _identitySub);
        var storedEntitlementId = await _database.ScalarAsync<string>(
            EntitlementSql, Token, _identitySub, gameId);
        var storedPlatforms = await _database.ScalarAsync<string[]>(
            PlatformsSql, Token, _identitySub, gameId);

        Assert.Equal(1L, rowCount);
        Assert.Equal(winningEntitlementId, storedEntitlementId);
        Assert.Equal([TitlePlatform.Ps4, TitlePlatform.Ps5], storedPlatforms);
    }

    [Fact]
    public async Task UpsertDownloadSizesAsync_ResolvesTheGameThroughTheCallersEntryTitleId_AndKeepsTheLargestSizePerPlatform()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var titleId = Generated.NewPs3TitleId();
        var smaller = Generated.NewDownloadSizeBytes();
        var larger = smaller + 1;
        var repository = new LibraryRepository(_database.DataSource);
        await repository.UpsertEntriesAsync(
            _identitySub,
            [LibraryEntryRow.Create(gameId, false, false, null, Generated.NewEntitlementId(), null, titleId, [TitlePlatform.Ps3], true)],
            Token);
        var sizes = new List<EntitlementDownloadSize>
        {
            new(Generated.NewEntitlementId(), titleId, TitlePlatform.Ps3, smaller),
            new(Generated.NewEntitlementId(), titleId, TitlePlatform.Ps3, larger),
        };

        var written = await repository.UpsertDownloadSizesAsync(_identitySub, sizes, Token);

        var stored = await _database.ScalarAsync<long>(DownloadSizeSql, Token, gameId, TitlePlatform.Ps3);

        Assert.Equal(1, written);
        Assert.Equal(larger, stored);
    }

    [Fact]
    public async Task UpsertDownloadSizesAsync_WritesNothingForATitleTheCallerDoesNotHold()
    {
        var gameId = await CreateGameAsync(Generated.NewGameTitle());
        var repository = new LibraryRepository(_database.DataSource);
        var sizes = new List<EntitlementDownloadSize>
        {
            new(Generated.NewEntitlementId(), Generated.NewPs3TitleId(), TitlePlatform.Ps3, Generated.NewDownloadSizeBytes()),
        };

        var written = await repository.UpsertDownloadSizesAsync(_identitySub, sizes, Token);

        var stored = await _database.ScalarOrDefaultAsync<long>(DownloadSizeSql, Token, gameId, TitlePlatform.Ps3);

        Assert.Equal(0, written);
        Assert.Null(stored);
    }

    private async Task UpsertMinimalAsync(LibraryRepository repository, Guid gameId) =>
        await repository.UpsertEntryAsync(
            _identitySub,
            gameId,
            nativePs5: true,
            ps4Eligible: false,
            ownedEdition: null,
            winningEntitlementId: Generated.NewEntitlementId(),
            productId: null,
            titleId: null,
            platforms: [TitlePlatform.Ps5],
            isActive: true,
            cancellationToken: Token);

    private async Task<Guid> CreateGameAsync(string canonicalTitle)
    {
        var gameId = Guid.NewGuid();
        await _database.ExecuteAsync(
            InsertGameSql, Token, gameId, canonicalTitle, canonicalTitle.ToLowerInvariant());
        _createdGames.Add(gameId);
        return gameId;
    }
}
