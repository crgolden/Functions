namespace Functions.Tests.Integration;

using Functions.Curator.Library;
using TestSupport;

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

    private const string NativePs5Sql =
        "SELECT native_ps5 FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

    private const string Ps4EligibleSql =
        "SELECT ps4_eligible FROM library_entries WHERE identity_sub = $1 AND game_id = $2";

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

    private const string DeleteGameSql = "DELETE FROM games WHERE game_id = $1";

    private const int TrophyPercentComplete = 42;

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
        // Arrange
        var gameId = await CreateGameAsync(TestValues.NewGameTitle());
        var storedTitleId = TestValues.NewTitleId();
        var repository = new LibraryRepository(_database.DataSource);

        // Act
        await repository.UpsertEntryAsync(
            _identitySub.ToString(),
            gameId,
            nativePs5: true,
            ps4Eligible: true,
            ownedEdition: TestValues.NewOwnedEdition(),
            winningEntitlementId: TestValues.NewEntitlementId(),
            productId: TestValues.NewProductId(),
            titleId: storedTitleId,
            platforms: ["PS5", "PS4"],
            isActive: true,
            cancellationToken: Token);

        // Assert
        var platforms = await _database.ScalarAsync<string[]>(PlatformsSql, Token, _identitySub, Guid.Parse(gameId));
        var titleId = await _database.ScalarAsync<string>(TitleIdSql, Token, _identitySub, Guid.Parse(gameId));

        Assert.Equal(["PS4", "PS5"], platforms);
        Assert.Equal(storedTitleId, titleId);
    }

    [Fact]
    public async Task UpsertEntryAsync_WhenARepullDropsAPlatform_DeletesOnlyThePlatformNoLongerOwned()
    {
        // Arrange
        var gameId = await CreateGameAsync(TestValues.NewGameTitle());
        var ownedEdition = TestValues.NewOwnedEdition();
        var entitlementId = TestValues.NewEntitlementId();
        var productId = TestValues.NewProductId();
        var titleId = TestValues.NewTitleId();
        var repository = new LibraryRepository(_database.DataSource);
        await repository.UpsertEntryAsync(
            _identitySub.ToString(),
            gameId,
            nativePs5: true,
            ps4Eligible: true,
            ownedEdition: ownedEdition,
            winningEntitlementId: entitlementId,
            productId: productId,
            titleId: titleId,
            platforms: ["PS5", "PS4"],
            isActive: true,
            cancellationToken: Token);

        // Act
        await repository.UpsertEntryAsync(
            _identitySub.ToString(),
            gameId,
            nativePs5: true,
            ps4Eligible: false,
            ownedEdition: ownedEdition,
            winningEntitlementId: entitlementId,
            productId: productId,
            titleId: titleId,
            platforms: ["PS5"],
            isActive: true,
            cancellationToken: Token);

        // Assert
        var platforms = await _database.ScalarAsync<string[]>(PlatformsSql, Token, _identitySub, Guid.Parse(gameId));
        var entries = await _database.ScalarAsync<long>(EntryCountSql, Token, _identitySub);

        Assert.Equal(["PS5"], platforms);
        Assert.Equal(1L, entries);
    }

    [Fact]
    public async Task UpsertEntryAsync_ForAGameAlreadyStored_UpdatesTheRowInPlace()
    {
        // Arrange
        var gameId = await CreateGameAsync(TestValues.NewGameTitle());
        var titleId = TestValues.NewTitleId();
        var repository = new LibraryRepository(_database.DataSource);
        await repository.UpsertEntryAsync(
            _identitySub.ToString(),
            gameId,
            nativePs5: false,
            ps4Eligible: true,
            ownedEdition: TestValues.NewOwnedEdition(),
            winningEntitlementId: TestValues.NewEntitlementId(),
            productId: TestValues.NewProductId(),
            titleId: titleId,
            platforms: ["PS4"],
            isActive: true,
            cancellationToken: Token);

        // Act
        await repository.UpsertEntryAsync(
            _identitySub.ToString(),
            gameId,
            nativePs5: true,
            ps4Eligible: true,
            ownedEdition: TestValues.NewOwnedEdition(),
            winningEntitlementId: TestValues.NewEntitlementId(),
            productId: TestValues.NewProductId(),
            titleId: titleId,
            platforms: ["PS5"],
            isActive: true,
            cancellationToken: Token);

        // Assert
        var nativePs5 = await _database.ScalarAsync<bool>(NativePs5Sql, Token, _identitySub, Guid.Parse(gameId));
        var entries = await _database.ScalarAsync<long>(EntryCountSql, Token, _identitySub);

        Assert.True(nativePs5);
        Assert.Equal(1L, entries);
    }

    [Fact]
    public async Task GetUnmatchedGameIdsAsync_CastsTheIdArrayAndReturnsOnlyEntriesWithNoTrophyMatch()
    {
        // Arrange
        var matched = await CreateGameAsync(TestValues.NewGameTitle());
        var unmatched = await CreateGameAsync(TestValues.NewGameTitle());
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, matched);
        await UpsertMinimalAsync(repository, unmatched);
        await repository.SetTrophyMatchAsync(
            _identitySub.ToString(),
            matched,
            TestValues.NewNpCommunicationId(),
            TrophyMatchService.ExactMatchMethod,
            null,
            Token);

        // Act
        var result = await repository.GetUnmatchedGameIdsAsync(
            _identitySub.ToString(), [matched, unmatched], Token);

        // Assert
        Assert.Equal([unmatched], result);
    }

    [Fact]
    public async Task SetTrophyMatchAsync_WithAPercent_StampsTheProgressFetchedTimestamp()
    {
        // Arrange
        var gameId = await CreateGameAsync(TestValues.NewGameTitle());
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, gameId);

        // Act
        await repository.SetTrophyMatchAsync(
            _identitySub.ToString(),
            gameId,
            TestValues.NewNpCommunicationId(),
            TrophyMatchService.FuzzyMatchMethod,
            TrophyPercentComplete,
            Token);

        // Assert
        var percent = await _database.ScalarAsync<short>(PercentSql, Token, _identitySub, Guid.Parse(gameId));
        var method = await _database.ScalarAsync<string>(MatchMethodSql, Token, _identitySub, Guid.Parse(gameId));
        var fetchedIsNull = await _database.ScalarAsync<bool>(
            ProgressFetchedIsNullSql, Token, _identitySub, Guid.Parse(gameId));

        Assert.Equal((short)TrophyPercentComplete, percent);
        Assert.Equal(TrophyMatchService.FuzzyMatchMethod, method);
        Assert.False(fetchedIsNull);
    }

    [Fact]
    public async Task SetTrophyMatchAsync_WithANullPercent_LeavesTheProgressTimestampAlone()
    {
        // Arrange
        var gameId = await CreateGameAsync(TestValues.NewGameTitle());
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, gameId);

        // Act
        await repository.SetTrophyMatchAsync(
            _identitySub.ToString(),
            gameId,
            TestValues.NewNpCommunicationId(),
            TrophyMatchService.ExactMatchMethod,
            null,
            Token);

        // Assert
        var fetchedIsNull = await _database.ScalarAsync<bool>(
            ProgressFetchedIsNullSql, Token, _identitySub, Guid.Parse(gameId));

        Assert.True(fetchedIsNull);
    }

    [Fact]
    public async Task RefreshTrophyProgressAsync_UpdatesEveryMatchedNpCommunicationIdAndReportsTheCount()
    {
        // Arrange
        var gameId = await CreateGameAsync(TestValues.NewGameTitle());
        var npCommunicationId = TestValues.NewNpCommunicationId();
        var refreshedProgress = TestValues.NewTrophyProgress();
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, gameId);
        await repository.SetTrophyMatchAsync(
            _identitySub.ToString(),
            gameId,
            npCommunicationId,
            TrophyMatchService.ExactMatchMethod,
            null,
            Token);

        // Act
        var updated = await repository.RefreshTrophyProgressAsync(
            _identitySub.ToString(),
            new Dictionary<string, int> { [npCommunicationId] = refreshedProgress },
            Token);

        // Assert
        var percent = await _database.ScalarAsync<short>(PercentSql, Token, _identitySub, Guid.Parse(gameId));

        Assert.Equal(1, updated);
        Assert.Equal((short)refreshedProgress, percent);
    }

    [Fact]
    public async Task GetGamesForContinuationAsync_JoinsGamesAndReturnsTheCanonicalTitle()
    {
        // Arrange
        var canonicalTitle = TestValues.NewGameTitle();
        var gameId = await CreateGameAsync(canonicalTitle);
        var repository = new LibraryRepository(_database.DataSource);
        await UpsertMinimalAsync(repository, gameId);

        // Act
        var games = await repository.GetGamesForContinuationAsync(
            _identitySub.ToString(), [gameId], Token);

        // Assert
        var only = Assert.Single(games);

        Assert.Equal(gameId, only.GameId);
        Assert.Equal(canonicalTitle, only.Title);
    }

    [Fact]
    public async Task UpsertEntriesAsync_WritesPsnSourcedRowsCarryingTheirEntitlementId()
    {
        // Arrange
        var gameId = await CreateGameAsync(TestValues.NewGameTitle());
        var sourcedEntitlementId = TestValues.NewEntitlementId();
        var repository = new LibraryRepository(_database.DataSource);
        var entries = new List<LibraryEntryRow>
        {
            LibraryEntryRow.Create(gameId, true, false, null, sourcedEntitlementId, null, null, ["PS5"], true),
        };

        // Act
        await repository.UpsertEntriesAsync(_identitySub.ToString(), entries, Token);

        // Assert
        var source = await _database.ScalarAsync<string>(SourceSql, Token, _identitySub, Guid.Parse(gameId));
        var entitlement = await _database.ScalarAsync<string>(
            EntitlementSql, Token, _identitySub, Guid.Parse(gameId));

        Assert.Equal("psn", source);
        Assert.Equal(sourcedEntitlementId, entitlement);
    }

    [Fact]
    public async Task UpsertEntriesAsync_TwoCanonicalGamesResolvingToOneGameId_MergesBothPlatformsOntoOneRow()
    {
        // Arrange
        var duplicatedGameTitle = Guid.NewGuid().ToString();
        var gameId = await CreateGameAsync(duplicatedGameTitle);
        var repository = new LibraryRepository(_database.DataSource);
        var supersededEntitlementId = Guid.NewGuid().ToString();
        var winningEntitlementId = Guid.NewGuid().ToString();
        var entries = new List<LibraryEntryRow>
        {
            LibraryEntryRow.Create(gameId, false, true, null, supersededEntitlementId, null, null, ["PS4"], true),
            LibraryEntryRow.Create(gameId, true, false, null, winningEntitlementId, null, null, ["PS5"], true),
        };

        // Act
        await repository.UpsertEntriesAsync(_identitySub.ToString(), entries, Token);

        // Assert
        var rowCount = await _database.ScalarAsync<long>(EntryCountSql, Token, _identitySub);
        var storedEntitlementId = await _database.ScalarAsync<string>(
            EntitlementSql, Token, _identitySub, Guid.Parse(gameId));
        var storedNativePs5 = await _database.ScalarAsync<bool>(
            NativePs5Sql, Token, _identitySub, Guid.Parse(gameId));
        var storedPs4Eligible = await _database.ScalarAsync<bool>(
            Ps4EligibleSql, Token, _identitySub, Guid.Parse(gameId));
        var storedPlatforms = await _database.ScalarAsync<string[]>(
            PlatformsSql, Token, _identitySub, Guid.Parse(gameId));

        Assert.Equal(1L, rowCount);
        Assert.Equal(winningEntitlementId, storedEntitlementId);
        Assert.True(storedNativePs5);
        Assert.True(storedPs4Eligible);
        Assert.Equal(["PS4", "PS5"], storedPlatforms);
    }

    private async Task UpsertMinimalAsync(LibraryRepository repository, string gameId) =>
        await repository.UpsertEntryAsync(
            _identitySub.ToString(),
            gameId,
            nativePs5: true,
            ps4Eligible: false,
            ownedEdition: null,
            winningEntitlementId: "ENT-" + gameId,
            productId: null,
            titleId: null,
            platforms: ["PS5"],
            isActive: true,
            cancellationToken: Token);

    private async Task<string> CreateGameAsync(string canonicalTitle)
    {
        var gameId = Guid.NewGuid();
        await _database.ExecuteAsync(
            InsertGameSql, Token, gameId, canonicalTitle, canonicalTitle.ToLowerInvariant());
        _createdGames.Add(gameId);
        return gameId.ToString();
    }
}
