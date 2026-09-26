namespace Functions.Tests.Integration;

using Functions.Curator.Catalog;
using Functions.Curator.Enrichment;
using Functions.Curator.Psn;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class CatalogRepositoryTests : IAsyncLifetime
{
    private const string FranchisePassName = CurationPassNames.FranchiseReclassification;

    private const string InsertLibraryEntrySql = """
        INSERT INTO library_entries (identity_sub, game_id, winning_entitlement_id, title_id)
        VALUES ($1, $2, $3, $4)
        """;

    private const string InsertPsnCacheSql =
        "INSERT INTO psn_catalog_cache (title_id, game_id) VALUES ($1, $2)";

    private const string InsertEditionRankSql =
        "INSERT INTO edition_ranks (keyword, rank) VALUES ($1, $2)";

    private const string InsertNameOverrideSql =
        "INSERT INTO game_name_overrides (concept_id, product_id, override_name, reason) VALUES ($1, $2, $3, $4)";

    private const string InsertGlobalExclusionSql =
        "INSERT INTO global_exclusions (concept_id, reason) VALUES ($1, $2)";

    private const string NormalizedTitleSql = "SELECT normalized_title FROM games WHERE game_id = $1";

    private const string CanonicalTitleSql = "SELECT canonical_title FROM games WHERE game_id = $1";

    private const string FranchiseIsNullSql = "SELECT franchise IS NULL FROM games WHERE game_id = $1";

    private const string FranchiseSql = "SELECT franchise FROM games WHERE game_id = $1";

    private const string ContentKindSql = "SELECT content_kind FROM games WHERE game_id = $1";

    private const string ConceptProductIdSql = "SELECT product_id FROM game_concepts WHERE concept_id = $1";

    private const string ConceptGameIdSql = "SELECT game_id FROM game_concepts WHERE concept_id = $1";

    private const string ConceptLinkCountSql =
        "SELECT count(*) FROM game_concepts WHERE concept_id = $1 AND game_id = $2";

    private const string FranchiseRuleCountSql = "SELECT count(*) FROM franchise_rules";

    private const string GamesByNormalizedTitleCountSql = "SELECT count(*) FROM games WHERE normalized_title = $1";

    private const string FingerprintSql =
        "SELECT rules_fingerprint FROM curation_rule_pass_state WHERE pass_name = $1";

    private const string DeleteNameOverrideSql = "DELETE FROM game_name_overrides WHERE concept_id = $1";
    private const string DeleteGlobalExclusionSql = "DELETE FROM global_exclusions WHERE concept_id = $1";
    private const string DeleteConceptSql = "DELETE FROM game_concepts WHERE concept_id = $1";
    private const string DeletePsnCacheSql = "DELETE FROM psn_catalog_cache WHERE title_id = $1";
    private const string DeleteGameSql = "DELETE FROM games WHERE game_id = $1";
    private const string DeleteEditionRankSql = "DELETE FROM edition_ranks WHERE keyword = $1";
    private const string DeletePassStateSql = "DELETE FROM curation_rule_pass_state WHERE pass_name = $1";

    private readonly CuratorDatabase _database;
    private readonly List<Guid> _createdGames = [];
    private readonly List<string> _createdConcepts = [];
    private readonly List<string> _createdTitleIds = [];
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
        var title = Generated.NewCanonicalTitle();
        var repository = new CatalogRepository(_database.DataSource);
        var game = NewGame(title);

        var gameId = await UpsertTrackedAsync(repository, game);

        var normalized = await _database.ScalarAsync<string>(NormalizedTitleSql, Token, gameId);
        var canonical = await _database.ScalarAsync<string>(CanonicalTitleSql, Token, gameId);

        Assert.Equal(title.ToLowerInvariant(), normalized);
        Assert.Equal(title, canonical);
    }

    [Fact]
    public async Task UpsertGameAsync_ForTheSameCanonicalTitleTwice_ReturnsTheSameGameIdRatherThanDuplicating()
    {
        var title = Generated.NewCanonicalTitle();
        var repository = new CatalogRepository(_database.DataSource);
        var first = await UpsertTrackedAsync(repository, NewGame(title));

        var second = await UpsertTrackedAsync(repository, NewGame(title));

        var rows = await CountGamesWithTitleAsync(title);

        Assert.Equal(first, second);
        Assert.Equal(1L, rows);
    }

    [Fact]
    public async Task UpsertGameAsync_ForTwoDifferentTitles_CreatesTwoDistinctGames()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var first = await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitle()));

        var second = await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitle()));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task UpsertGameAsync_WhenTheTitleDiffersOnlyByCaseAndPadding_StillResolvesTheSameGame()
    {
        var title = Generated.NewCanonicalTitle();
        var repository = new CatalogRepository(_database.DataSource);
        var first = await UpsertTrackedAsync(repository, NewGame(title));

        var second = await UpsertTrackedAsync(repository, NewGame($"  {title.ToUpperInvariant()}  "));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task UpsertGameAsync_ForAConceptSeenUnderAnotherName_AddsASecondGameAndLeavesTheFirstAlone()
    {
        var conceptId = Generated.NewConceptId();
        var repository = new CatalogRepository(_database.DataSource);
        var originalTitle = Generated.NewCanonicalTitle();
        var original = NewGame(originalTitle) with { ConceptIds = [conceptId] };
        var first = await UpsertTrackedAsync(repository, original);
        var otherProductTitle = Generated.NewCanonicalTitle();
        var otherProduct = NewGame(otherProductTitle) with { ConceptIds = [conceptId] };

        var second = await UpsertTrackedAsync(repository, otherProduct);

        var firstCanonical = await _database.ScalarAsync<string>(CanonicalTitleSql, Token, first);
        var secondCanonical = await _database.ScalarAsync<string>(CanonicalTitleSql, Token, second);
        var firstLinks = await _database.ScalarAsync<long>(ConceptLinkCountSql, Token, conceptId, first);

        Assert.NotEqual(first, second);
        Assert.Equal(originalTitle, firstCanonical);
        Assert.Equal(otherProductTitle, secondCanonical);
        Assert.Equal(1L, firstLinks);
    }

    [Fact]
    public async Task UpsertGameAsync_KeepsAStoredContentKind_WhenALaterUpsertOfTheSameGameCarriesNone()
    {
        var title = Generated.NewCanonicalTitle();
        var repository = new CatalogRepository(_database.DataSource);
        var gameId = await UpsertTrackedAsync(repository, NewGame(title) with { ContentKind = ContentKind.MediaApp });

        await UpsertTrackedAsync(repository, NewGame(title));

        var stored = await _database.ScalarAsync<string>(ContentKindSql, Token, gameId);

        Assert.Equal(ContentKinds.MediaApp, stored);
    }

    [Fact]
    public async Task UpsertGameAsync_StoresTheGameKind_WhichTheCheckConstraintMustAdmit()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var game = NewGame(Generated.NewCanonicalTitle()) with { ContentKind = ContentKind.Game };

        var gameId = await UpsertTrackedAsync(repository, game);

        var stored = await _database.ScalarAsync<string>(ContentKindSql, Token, gameId);
        Assert.Equal(ContentKinds.Game, stored);
    }

    [Fact]
    public async Task UpsertGameAsync_WithABlankFranchise_StoresNullRatherThanAnEmptyString()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var game = NewGame(Generated.NewCanonicalTitle()) with { Franchise = Generated.NewBlankRun() };

        var gameId = await UpsertTrackedAsync(repository, game);

        var isNull = await _database.ScalarAsync<bool>(FranchiseIsNullSql, Token, gameId);

        Assert.True(isNull);
    }

    [Fact]
    public async Task UpsertGameAsync_ForAConceptSeenAgainWithAnotherProductId_KeepsTheLinkItFirstRecorded()
    {
        var conceptId = Generated.NewConceptId();
        var title = Generated.NewCanonicalTitle();
        var repository = new CatalogRepository(_database.DataSource);
        var firstProductId = Generated.NewProductId();
        var first = NewGame(title) with { ConceptIds = [conceptId], ProductId = firstProductId };
        var second = NewGame(title) with { ConceptIds = [conceptId], ProductId = Generated.NewProductId() };
        var gameId = await UpsertTrackedAsync(repository, first);

        var resolvedGameId = await UpsertTrackedAsync(repository, second);

        var productId = await _database.ScalarAsync<string>(ConceptProductIdSql, Token, conceptId);
        var linkedGameId = await _database.ScalarAsync<Guid>(ConceptGameIdSql, Token, conceptId);

        Assert.Equal(gameId, resolvedGameId);
        Assert.Equal(firstProductId, productId);
        Assert.Equal(gameId, linkedGameId);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_PrefersThePsnCatalogTitleIdOverTheLibraryEntryOne()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var gameId = await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitle()));
        var cacheTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        await AddPsnCacheTitleAsync(cacheTitleId, gameId);
        await AddLibraryEntryAsync(gameId, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix));

        var games = await repository.ListAllGameIdsAndTitlesAsync(Token);

        var stored = Assert.Single(games, game => game.GameId == gameId);

        Assert.Equal(cacheTitleId, stored.TitleId);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_WithNoCatalogRow_FallsBackToTheLibraryEntryTitleId()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var gameId = await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitle()));
        var libraryTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        await AddLibraryEntryAsync(gameId, libraryTitleId);

        var games = await repository.ListAllGameIdsAndTitlesAsync(Token);

        var stored = Assert.Single(games, game => game.GameId == gameId);

        Assert.Equal(libraryTitleId, stored.TitleId);
    }

    [Fact]
    public async Task ListAllGameIdsAndTitlesAsync_WithNeitherSource_YieldsANullTitleId()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var gameId = await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitle()));

        var games = await repository.ListAllGameIdsAndTitlesAsync(Token);

        var stored = Assert.Single(games, game => game.GameId == gameId);

        Assert.Null(stored.TitleId);
    }

    [Fact]
    public async Task SetFranchiseRulesFingerprintAsync_RoundTripsAndOverwritesOnASecondWrite()
    {
        var repository = new CatalogRepository(_database.DataSource);
        _createdPassNames.Add(FranchisePassName);
        var overwrittenFingerprint = Generated.NewFingerprint();
        await repository.SetFranchiseRulesFingerprintAsync(Generated.NewFingerprint(), Token);

        await repository.SetFranchiseRulesFingerprintAsync(overwrittenFingerprint, Token);

        var read = await repository.GetFranchiseRulesFingerprintAsync(Token);
        var stored = await _database.ScalarAsync<string>(FingerprintSql, Token, FranchisePassName);

        Assert.Equal(overwrittenFingerprint, read);
        Assert.Equal(overwrittenFingerprint, stored);
    }

    [Fact]
    public async Task GetFranchiseRulesFingerprintAsync_WhenThePassHasNeverRun_ReturnsNull()
    {
        var repository = new CatalogRepository(_database.DataSource);

        var fingerprint = await repository.GetFranchiseRulesFingerprintAsync(Token);

        Assert.Null(fingerprint);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_StoresTheFranchiseMatchedByTheRules()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var franchiseKeyword = Generated.NewFranchiseKeyword();
        var franchiseName = Generated.NewFranchiseName();
        var franchiseRuleId = Guid.NewGuid();
        var gameId = await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitleContaining(franchiseKeyword)));
        var rules = new List<FranchiseRule>
        {
            new(franchiseRuleId, franchiseKeyword, franchiseName, 0),
        };

        var updated = await repository.ReclassifyFranchiseAsync(rules, Token);

        var franchise = await _database.ScalarAsync<string>(FranchiseSql, Token, gameId);

        Assert.Equal(1, updated);
        Assert.Equal(franchiseName, franchise);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_PrefersTheLowerPriorityRuleWhenTwoMatch()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var franchiseKeyword = Generated.NewFranchiseKeyword();
        var winningFranchiseName = Generated.NewFranchiseName();
        var winnerPriority = Random.Shared.Next(0, 5);
        var priorityGap = Random.Shared.Next(1, 10);
        var loserPriority = winnerPriority + priorityGap;
        var losingRuleId = Guid.NewGuid();
        var losingFranchiseName = Generated.NewFranchiseName();
        var winningRuleId = Guid.NewGuid();
        var gameId = await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitleContaining(franchiseKeyword)));
        var rules = new List<FranchiseRule>
        {
            new(losingRuleId, franchiseKeyword, losingFranchiseName, loserPriority),
            new(winningRuleId, franchiseKeyword, winningFranchiseName, winnerPriority),
        };

        await repository.ReclassifyFranchiseAsync(rules, Token);

        var franchise = await _database.ScalarAsync<string>(FranchiseSql, Token, gameId);

        Assert.Equal(winningFranchiseName, franchise);
    }

    [Fact]
    public async Task ReclassifyFranchiseAsync_WhenTheStoredFranchiseAlreadyMatches_ReportsNothingUpdated()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var franchiseKeyword = Generated.NewFranchiseKeyword();
        var franchiseRuleId = Guid.NewGuid();
        await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitleContaining(franchiseKeyword)));
        var rules = new List<FranchiseRule>
        {
            new(franchiseRuleId, franchiseKeyword, Generated.NewFranchiseName(), 0),
        };
        await repository.ReclassifyFranchiseAsync(rules, Token);

        var updated = await repository.ReclassifyFranchiseAsync(rules, Token);

        Assert.Equal(0, updated);
    }

    [Fact]
    public async Task GetEditionRanksAsync_ReadsEachKeywordWithItsRank()
    {
        var keyword = $"integration-{Guid.NewGuid():N}";
        var rank = Random.Shared.Next(1, 100);
        await _database.ExecuteAsync(InsertEditionRankSql, Token, keyword, rank);
        _createdEditionKeywords.Add(keyword);
        var repository = new CatalogRepository(_database.DataSource);

        var ranks = await repository.GetEditionRanksAsync(Token);

        Assert.Equal(rank, ranks[keyword]);
    }

    [Fact]
    public async Task GetNameOverridesAsync_ReadsAnOverrideKeyedByTheConceptAndTheProduct()
    {
        var conceptId = Generated.NewConceptId();
        var productId = Generated.NewProductId();
        var overrideName = Generated.NewOverrideName();
        var repository = new CatalogRepository(_database.DataSource);
        await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitle()) with { ConceptIds = [conceptId] });
        await _database.ExecuteAsync(InsertNameOverrideSql, Token, conceptId, productId, overrideName, Generated.NewToken());

        var overrides = await repository.GetNameOverridesAsync(Token);

        Assert.Equal(overrideName, overrides[new NameOverrideKey(conceptId, productId)]);
    }

    [Fact]
    public async Task GetGloballyExcludedConceptIdsAsync_ReadsEveryGloballyExcludedConcept()
    {
        var conceptId = Generated.NewConceptId();
        var repository = new CatalogRepository(_database.DataSource);
        await UpsertTrackedAsync(repository, NewGame(Generated.NewCanonicalTitle()) with { ConceptIds = [conceptId] });
        await _database.ExecuteAsync(InsertGlobalExclusionSql, Token, conceptId, Generated.NewToken());

        var excluded = await repository.GetGloballyExcludedConceptIdsAsync(Token);

        Assert.Contains(conceptId, excluded);
    }

    [Fact]
    public async Task ListFranchiseRulesAsync_ReadsTheSeededRuleSet()
    {
        var repository = new CatalogRepository(_database.DataSource);
        var expected = await _database.ScalarAsync<long>(FranchiseRuleCountSql, Token);

        var rules = await repository.ListFranchiseRulesAsync(Token);

        Assert.Equal(expected, rules.Count);
    }

    private static CanonicalGame NewGame(string canonicalTitle) =>
        new(canonicalTitle, true, false, null, null, [], Generated.NewEntitlementId());

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
            GamesByNormalizedTitleCountSql, Token, title.ToLowerInvariant());

    private async Task<Guid> UpsertTrackedAsync(CatalogRepository repository, CanonicalGame game)
    {
        var gameId = await repository.UpsertGameAsync(game, Token);
        _createdGames.Add(gameId);
        _createdConcepts.AddRange(game.ConceptIds);
        return gameId;
    }

    private async Task AddPsnCacheTitleAsync(string titleId, Guid gameId)
    {
        await _database.ExecuteAsync(InsertPsnCacheSql, Token, titleId, gameId);
        _createdTitleIds.Add(titleId);
    }

    private async Task AddLibraryEntryAsync(Guid gameId, string titleId) =>
        await _database.ExecuteAsync(
            InsertLibraryEntrySql, Token, _identitySub, gameId, Generated.NewEntitlementId(), titleId);
}
