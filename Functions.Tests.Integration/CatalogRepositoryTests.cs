namespace Functions.Tests.Integration;

using Functions.Curator.Catalog;
using TestSupport;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class CatalogRepositoryTests : IAsyncLifetime
{
    private const string FranchisePassName = "franchise_reclassification";

    private const string InsertLibraryEntrySql = """
        INSERT INTO library_entries (identity_sub, game_id, winning_entitlement_id, title_id)
        VALUES ($1, $2, $3, $4)
        """;

    private const string InsertPsnCacheSql =
        "INSERT INTO psn_catalog_cache (title_id, game_id) VALUES ($1, $2)";

    private const string InsertExclusionRuleSql =
        "INSERT INTO exclusion_rules (rule_id, rule_type, pattern) VALUES ($1, $2, $3)";

    private const string InsertEditionRankSql =
        "INSERT INTO edition_ranks (keyword, rank) VALUES ($1, $2)";

    private const string InsertNameOverrideSql =
        "INSERT INTO game_name_overrides (concept_id, override_name, reason) VALUES ($1, $2, $3)";

    private const string InsertGlobalExclusionSql =
        "INSERT INTO global_exclusions (concept_id, reason) VALUES ($1, $2)";

    private const string NormalizedTitleSql = "SELECT normalized_title FROM games WHERE game_id = $1";

    private const string CanonicalTitleSql = "SELECT canonical_title FROM games WHERE game_id = $1";

    private const string FranchiseIsNullSql = "SELECT franchise IS NULL FROM games WHERE game_id = $1";

    private const string FranchiseSql = "SELECT franchise FROM games WHERE game_id = $1";

    private const string ConceptProductIdSql = "SELECT product_id FROM game_concepts WHERE concept_id = $1";

    private const string ConceptGameIdSql = "SELECT game_id FROM game_concepts WHERE concept_id = $1";

    private const string FingerprintSql =
        "SELECT rules_fingerprint FROM curation_rule_pass_state WHERE pass_name = $1";

    private const string DeleteNameOverrideSql = "DELETE FROM game_name_overrides WHERE concept_id = $1";
    private const string DeleteGlobalExclusionSql = "DELETE FROM global_exclusions WHERE concept_id = $1";
    private const string DeleteConceptSql = "DELETE FROM game_concepts WHERE concept_id = $1";
    private const string DeletePsnCacheSql = "DELETE FROM psn_catalog_cache WHERE title_id = $1";
    private const string DeleteGameSql = "DELETE FROM games WHERE game_id = $1";
    private const string DeleteExclusionRuleSql = "DELETE FROM exclusion_rules WHERE rule_id = $1";
    private const string DeleteEditionRankSql = "DELETE FROM edition_ranks WHERE keyword = $1";
    private const string DeletePassStateSql = "DELETE FROM curation_rule_pass_state WHERE pass_name = $1";

    private readonly CuratorDatabase _database;
    private readonly List<Guid> _createdGames = [];
    private readonly List<string> _createdConcepts = [];
    private readonly List<string> _createdTitleIds = [];
    private readonly List<Guid> _createdExclusionRules = [];
    private readonly List<string> _createdEditionKeywords = [];
    private readonly List<string> _createdPassNames = [];
    private Guid _identitySub;

    public CatalogRepositoryTests(CuratorDatabase database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _identitySub = await _database.CreateUserAsync(Token);

    public async ValueTask DisposeAsync()
    {
        await DeleteRowsCascadingFromTheUserAsync();
        await DeleteOverridesAndExclusionsBeforeConceptsAsync();
        await DeleteRowsKeyedIndependentlyOfGamesAsync();
        await DeleteGamesAfterConceptsAsync();
    }

    [Fact]
    public async Task UpsertGameAsync_ForANewTitle_InsertsTheGameAndStoresTheNormalizedTitle()
    {
        // Arrange
        var title = NewTitle("Game");
        var repository = new CatalogRepository(_database.DataSource);
        var game = NewGame(title);

        // Act
        var gameId = await UpsertTrackedAsync(repository, game);

        // Assert
        var normalized = await _database.ScalarAsync<string>(NormalizedTitleSql, Token, Guid.Parse(gameId));
        var canonical = await _database.ScalarAsync<string>(CanonicalTitleSql, Token, Guid.Parse(gameId));

        Assert.Equal(title.ToLowerInvariant(), normalized);
        Assert.Equal(title, canonical);
    }

    [Fact]
    public async Task UpsertGameAsync_ForTheSameCanonicalTitleTwice_ReturnsTheSameGameIdRatherThanDuplicating()
    {
        // Arrange
        var title = NewTitle("Game");
        var repository = new CatalogRepository(_database.DataSource);
        var first = await UpsertTrackedAsync(repository, NewGame(title));

        // Act
        var second = await UpsertTrackedAsync(repository, NewGame(title));

        // Assert
        var rows = await CountGamesWithTitleAsync(title);

        Assert.Equal(first, second);
        Assert.Equal(1L, rows);
    }

    [Fact]
    public async Task UpsertGameAsync_ForTwoDifferentTitles_CreatesTwoDistinctGames()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var first = await UpsertTrackedAsync(repository, NewGame(NewTitle("Game")));

        // Act
        var second = await UpsertTrackedAsync(repository, NewGame(NewTitle("Game")));

        // Assert
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task UpsertGameAsync_WhenTheTitleDiffersOnlyByCaseAndPadding_StillResolvesTheSameGame()
    {
        // Arrange
        var title = NewTitle("Game");
        var repository = new CatalogRepository(_database.DataSource);
        var first = await UpsertTrackedAsync(repository, NewGame(title));

        // Act
        var second = await UpsertTrackedAsync(repository, NewGame($"  {title.ToUpperInvariant()}  "));

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task UpsertGameAsync_WhenAConceptIsAlreadyLinked_ResolvesThatGameAndUpdatesItsTitle()
    {
        // Arrange
        var conceptId = NewConceptId();
        var repository = new CatalogRepository(_database.DataSource);
        var original = NewGame(NewTitle("Game")) with { ConceptIds = [conceptId] };
        var first = await UpsertTrackedAsync(repository, original);
        var renamedTitle = NewTitle("Game");
        var renamed = NewGame(renamedTitle) with { ConceptIds = [conceptId] };

        // Act
        var second = await UpsertTrackedAsync(repository, renamed);

        // Assert
        var canonical = await _database.ScalarAsync<string>(CanonicalTitleSql, Token, Guid.Parse(first));

        Assert.Equal(first, second);
        Assert.Equal(renamedTitle, canonical);
    }

    [Fact]
    public async Task UpsertGameAsync_WithABlankFranchise_StoresNullRatherThanAnEmptyString()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var game = NewGame(NewTitle("Game")) with { Franchise = "   " };

        // Act
        var gameId = await UpsertTrackedAsync(repository, game);

        // Assert
        var isNull = await _database.ScalarAsync<bool>(FranchiseIsNullSql, Token, Guid.Parse(gameId));

        Assert.True(isNull);
    }

    [Fact]
    public async Task UpsertGameAsync_ForAConceptSeenAgain_UpdatesItsProductIdInPlace()
    {
        // Arrange
        var conceptId = NewConceptId();
        var title = NewTitle("Game");
        var repository = new CatalogRepository(_database.DataSource);
        var updatedProductId = NewProductId();
        var first = NewGame(title) with { ConceptIds = [conceptId], ProductId = NewProductId() };
        var second = NewGame(title) with { ConceptIds = [conceptId], ProductId = updatedProductId };
        var gameId = await UpsertTrackedAsync(repository, first);

        // Act
        await UpsertTrackedAsync(repository, second);

        // Assert
        var productId = await _database.ScalarAsync<string>(ConceptProductIdSql, Token, conceptId);
        var linkedGameId = await _database.ScalarAsync<Guid>(ConceptGameIdSql, Token, conceptId);

        Assert.Equal(updatedProductId, productId);
        Assert.Equal(Guid.Parse(gameId), linkedGameId);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_PrefersThePsnCatalogTitleIdOverTheLibraryEntryOne()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var gameId = await UpsertTrackedAsync(repository, NewGame(NewTitle("Game")));
        var cacheTitleId = NewTitleId();
        await AddPsnCacheTitleAsync(cacheTitleId, gameId);
        await AddLibraryEntryAsync(gameId, NewTitleId());

        // Act
        var games = await repository.ListAllGameIdsAndTitlesAsync(Token);

        // Assert
        var stored = Assert.Single(games, game => string.Equals(game.GameId, gameId, StringComparison.Ordinal));

        Assert.Equal(cacheTitleId, stored.TitleId);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_WithNoCatalogRow_FallsBackToTheLibraryEntryTitleId()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var gameId = await UpsertTrackedAsync(repository, NewGame(NewTitle("Game")));
        var libraryTitleId = NewTitleId();
        await AddLibraryEntryAsync(gameId, libraryTitleId);

        // Act
        var games = await repository.ListAllGameIdsAndTitlesAsync(Token);

        // Assert
        var stored = Assert.Single(games, game => string.Equals(game.GameId, gameId, StringComparison.Ordinal));

        Assert.Equal(libraryTitleId, stored.TitleId);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_WithNeitherSource_YieldsANullTitleId()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var gameId = await UpsertTrackedAsync(repository, NewGame(NewTitle("Game")));

        // Act
        var games = await repository.ListAllGameIdsAndTitlesAsync(Token);

        // Assert
        var stored = Assert.Single(games, game => string.Equals(game.GameId, gameId, StringComparison.Ordinal));

        Assert.Null(stored.TitleId);
    }

    [Fact]
    public async Task SetFranchiseRulesFingerprintAsync_RoundTripsAndOverwritesOnASecondWrite()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        _createdPassNames.Add(FranchisePassName);
        var overwrittenFingerprint = NewFingerprint();
        await repository.SetFranchiseRulesFingerprintAsync(NewFingerprint(), Token);

        // Act
        await repository.SetFranchiseRulesFingerprintAsync(overwrittenFingerprint, Token);

        // Assert
        var read = await repository.GetFranchiseRulesFingerprintAsync(Token);
        var stored = await _database.ScalarAsync<string>(FingerprintSql, Token, FranchisePassName);

        Assert.Equal(overwrittenFingerprint, read);
        Assert.Equal(overwrittenFingerprint, stored);
    }

    [Fact]
    public async Task GetFranchiseRulesFingerprintAsync_WhenThePassHasNeverRun_ReturnsNull()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);

        // Act
        var fingerprint = await repository.GetFranchiseRulesFingerprintAsync(Token);

        // Assert
        Assert.Null(fingerprint);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_StoresTheFranchiseMatchedByTheRules()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var franchiseKeyword = NewFranchiseKeyword();
        var franchiseName = NewFranchiseName();
        var gameId = await UpsertTrackedAsync(repository, NewGame(NewTitle($"Saga {franchiseKeyword}")));
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), franchiseKeyword, franchiseName, 0),
        };

        // Act
        var updated = await repository.ReclassifyFranchiseAsync(rules, Token);

        // Assert
        var franchise = await _database.ScalarAsync<string>(FranchiseSql, Token, Guid.Parse(gameId));

        Assert.Equal(1, updated);
        Assert.Equal(franchiseName, franchise);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_PrefersTheLowerPriorityRuleWhenTwoMatch()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var franchiseKeyword = NewFranchiseKeyword();
        var winningFranchiseName = NewFranchiseName();
        var winnerPriority = Random.Shared.Next(0, 5);
        var loserPriority = winnerPriority + Random.Shared.Next(1, 10);
        var gameId = await UpsertTrackedAsync(repository, NewGame(NewTitle($"Saga {franchiseKeyword}")));
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), franchiseKeyword, NewFranchiseName(), loserPriority),
            new(Guid.NewGuid(), franchiseKeyword, winningFranchiseName, winnerPriority),
        };

        // Act
        await repository.ReclassifyFranchiseAsync(rules, Token);

        // Assert
        var franchise = await _database.ScalarAsync<string>(FranchiseSql, Token, Guid.Parse(gameId));

        Assert.Equal(winningFranchiseName, franchise);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_WhenTheStoredFranchiseAlreadyMatches_ReportsNothingUpdated()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var franchiseKeyword = NewFranchiseKeyword();
        await UpsertTrackedAsync(repository, NewGame(NewTitle($"Saga {franchiseKeyword}")));
        var rules = new List<FranchiseRule>
        {
            new(Guid.NewGuid(), franchiseKeyword, NewFranchiseName(), 0),
        };
        await repository.ReclassifyFranchiseAsync(rules, Token);

        // Act
        var updated = await repository.ReclassifyFranchiseAsync(rules, Token);

        // Assert
        Assert.Equal(0, updated);
    }

    [Fact]
    public async Task ListExclusionRulesAsync_ReadsARuleThatSatisfiesTheRuleTypeCheck()
    {
        // Arrange
        var ruleId = Guid.NewGuid();
        var pattern = NewToken();
        await _database.ExecuteAsync(InsertExclusionRuleSql, Token, ruleId, "media_app", pattern);
        _createdExclusionRules.Add(ruleId);
        var repository = new CatalogRepository(_database.DataSource);

        // Act
        var rules = await repository.ListExclusionRulesAsync(Token);

        // Assert
        var stored = Assert.Single(rules, rule => rule.RuleId == ruleId);

        Assert.Equal("media_app", stored.RuleType);
        Assert.Equal(pattern, stored.Pattern);
    }

    [Fact]
    public async Task GetEditionRanksAsync_ReadsEachKeywordWithItsRank()
    {
        // Arrange
        var keyword = $"integration-{Guid.NewGuid():N}";
        var rank = Random.Shared.Next(1, 100);
        await _database.ExecuteAsync(InsertEditionRankSql, Token, keyword, rank);
        _createdEditionKeywords.Add(keyword);
        var repository = new CatalogRepository(_database.DataSource);

        // Act
        var ranks = await repository.GetEditionRanksAsync(Token);

        // Assert
        Assert.Equal(rank, ranks[keyword]);
    }

    [Fact]
    public async Task GetNameOverridesAsync_ReadsAnOverrideKeyedByAnExistingConcept()
    {
        // Arrange
        var conceptId = NewConceptId();
        var overrideName = NewOverrideName();
        var repository = new CatalogRepository(_database.DataSource);
        await UpsertTrackedAsync(repository, NewGame(NewTitle("Game")) with { ConceptIds = [conceptId] });
        await _database.ExecuteAsync(InsertNameOverrideSql, Token, conceptId, overrideName, NewToken());

        // Act
        var overrides = await repository.GetNameOverridesAsync(Token);

        // Assert
        Assert.Equal(overrideName, overrides[conceptId]);
    }

    [Fact]
    public async Task GetGloballyExcludedConceptIdsAsync_ReadsEveryGloballyExcludedConcept()
    {
        // Arrange
        var conceptId = NewConceptId();
        var repository = new CatalogRepository(_database.DataSource);
        await UpsertTrackedAsync(repository, NewGame(NewTitle("Game")) with { ConceptIds = [conceptId] });
        await _database.ExecuteAsync(InsertGlobalExclusionSql, Token, conceptId, NewToken());

        // Act
        var excluded = await repository.GetGloballyExcludedConceptIdsAsync(Token);

        // Assert
        Assert.Contains(conceptId, excluded);
    }

    [Fact]
    public async Task ListFranchiseRulesAsync_ReadsTheSeededRuleSet()
    {
        // Arrange
        var repository = new CatalogRepository(_database.DataSource);
        var expected = await _database.ScalarAsync<long>("SELECT count(*) FROM franchise_rules", Token);

        // Act
        var rules = await repository.ListFranchiseRulesAsync(Token);

        // Assert
        Assert.Equal(expected, rules.Count);
    }

    private static CanonicalGame NewGame(string canonicalTitle) =>
        new(canonicalTitle, true, false, null, null, [], NewEntitlementId());

    private static string NewTitle(string prefix) => TestValues.NewTitle(prefix);

    private static string NewConceptId() => TestValues.NewConceptId();

    private static string NewTitleId() => TestValues.NewTitleId();

    private static string NewProductId() => TestValues.NewProductId();

    private static string NewEntitlementId() => TestValues.NewEntitlementId();

    private static string NewFingerprint() => TestValues.NewFingerprint();

    private static string NewFranchiseKeyword() => TestValues.NewFranchiseKeyword();

    private static string NewFranchiseName() => TestValues.NewFranchiseName();

    private static string NewOverrideName() => TestValues.NewOverrideName();

    private static string NewToken() => TestValues.NewToken();

    private async Task DeleteRowsCascadingFromTheUserAsync() =>
        await _database.DeleteUserAsync(_identitySub, Token);

    private async Task DeleteOverridesAndExclusionsBeforeConceptsAsync()
    {
        foreach (var conceptId in _createdConcepts)
        {
            await _database.ExecuteAsync(DeleteNameOverrideSql, Token, conceptId);
            await _database.ExecuteAsync(DeleteGlobalExclusionSql, Token, conceptId);
            await _database.ExecuteAsync(DeleteConceptSql, Token, conceptId);
        }
    }

    private async Task DeleteRowsKeyedIndependentlyOfGamesAsync()
    {
        foreach (var titleId in _createdTitleIds)
        {
            await _database.ExecuteAsync(DeletePsnCacheSql, Token, titleId);
        }

        foreach (var ruleId in _createdExclusionRules)
        {
            await _database.ExecuteAsync(DeleteExclusionRuleSql, Token, ruleId);
        }

        foreach (var keyword in _createdEditionKeywords)
        {
            await _database.ExecuteAsync(DeleteEditionRankSql, Token, keyword);
        }

        foreach (var passName in _createdPassNames)
        {
            await _database.ExecuteAsync(DeletePassStateSql, Token, passName);
        }
    }

    private async Task DeleteGamesAfterConceptsAsync()
    {
        foreach (var gameId in _createdGames)
        {
            await _database.ExecuteAsync(DeleteGameSql, Token, gameId);
        }
    }

    private async Task<long> CountGamesWithTitleAsync(string title) =>
        await _database.ScalarAsync<long>(
            "SELECT count(*) FROM games WHERE normalized_title = $1", Token, title.ToLowerInvariant());

    private async Task<string> UpsertTrackedAsync(CatalogRepository repository, CanonicalGame game)
    {
        var gameId = await repository.UpsertGameAsync(game, Token);
        _createdGames.Add(Guid.Parse(gameId));
        _createdConcepts.AddRange(game.ConceptIds);
        return gameId;
    }

    private async Task AddPsnCacheTitleAsync(string titleId, string gameId)
    {
        await _database.ExecuteAsync(InsertPsnCacheSql, Token, titleId, Guid.Parse(gameId));
        _createdTitleIds.Add(titleId);
    }

    private async Task AddLibraryEntryAsync(string gameId, string titleId) =>
        await _database.ExecuteAsync(
            InsertLibraryEntrySql, Token, _identitySub, Guid.Parse(gameId), NewEntitlementId(), titleId);
}
