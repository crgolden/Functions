namespace Functions.Tests.Integration;

using Functions.Curator.Catalog;
using Functions.Curator.Psn;
using Functions.Curator.Store;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class StoreCatalogCrawlRepositoryTests : IAsyncLifetime
{
    private const string InsertGameSql = """
        INSERT INTO games (canonical_title, normalized_title, content_kind)
        VALUES ($1, $2, $3)
        RETURNING game_id
        """;

    private const string GameIdByNormalizedTitleSql =
        "SELECT game_id FROM games WHERE normalized_title = $1";

    private const string GameCountByNormalizedTitleSql =
        "SELECT count(*) FROM games WHERE normalized_title = $1";

    private const string ContentKindSql = "SELECT content_kind FROM games WHERE game_id = $1";

    private const string CoverSql = "SELECT store_cover_image_url FROM games WHERE game_id = $1";

    private const string CoverIsNullSql = "SELECT store_cover_image_url IS NULL FROM games WHERE game_id = $1";

    private const string EnrichmentCountSql = "SELECT count(*) FROM game_enrichment WHERE game_id = $1";

    private const string CacheProductIdSql = "SELECT store_product_id FROM psn_catalog_cache WHERE title_id = $1";

    private const string CacheGameIdSql = "SELECT game_id FROM psn_catalog_cache WHERE title_id = $1";

    private const string DeleteCacheSql = "DELETE FROM psn_catalog_cache WHERE title_id = $1";

    private const string DeleteEnrichmentSql = "DELETE FROM game_enrichment WHERE game_id = $1";

    private const string DeleteGameSql = "DELETE FROM games WHERE game_id = $1";

    private readonly CuratorDatabase _database;
    private readonly List<Guid> _createdGames = [];
    private readonly List<string> _createdTitleIds = [];

    public StoreCatalogCrawlRepositoryTests(CuratorDatabase database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var titleId in _createdTitleIds)
        {
            await _database.ExecuteAsync(DeleteCacheSql, CancellationToken.None, titleId);
        }

        foreach (var gameId in _createdGames)
        {
            await _database.ExecuteAsync(DeleteEnrichmentSql, CancellationToken.None, gameId);
            await _database.ExecuteAsync(DeleteGameSql, CancellationToken.None, gameId);
        }
    }

    [Fact]
    public async Task AdmitAsync_ForATitleTheCatalogDoesNotHold_CreatesTheGameAsAGameWithItsWalkedCover()
    {
        var title = Generated.NewCanonicalTitle();
        var cover = Generated.NewCoverImageAddress();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);

        var created = await new StoreCatalogCrawlRepository(_database.DataSource)
            .AdmitAsync([FullGame(title, cover, titleId)], Token);

        var gameId = await TrackAsync(title, titleId);
        Assert.Equal(1, created);
        Assert.Equal(ContentKinds.Game, await _database.ScalarAsync<string>(ContentKindSql, Token, gameId));
        Assert.Equal(cover, await _database.ScalarAsync<string>(CoverSql, Token, gameId));
    }

    [Fact]
    public async Task AdmitAsync_ForTheSameProductTwice_LeavesOneGameRow_AndReportsItCreatedOnce()
    {
        var title = Generated.NewCanonicalTitle();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var product = FullGame(title, Generated.NewCoverImageAddress(), titleId);
        var repository = new StoreCatalogCrawlRepository(_database.DataSource);

        var first = await repository.AdmitAsync([product], Token);
        var second = await repository.AdmitAsync([product], Token);

        await TrackAsync(title, titleId);
        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1L, await _database.ScalarAsync<long>(GameCountByNormalizedTitleSql, Token, NormalizedTitle(title)));
    }

    [Fact]
    public async Task AdmitAsync_OpensTheEnrichmentRowAndTheCatalogCacheRow_SoTheNightlyProductPassReachesTheGame()
    {
        var title = Generated.NewCanonicalTitle();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var product = FullGame(title, Generated.NewCoverImageAddress(), titleId);

        await new StoreCatalogCrawlRepository(_database.DataSource).AdmitAsync([product], Token);

        var gameId = await TrackAsync(title, titleId);
        Assert.Equal(1L, await _database.ScalarAsync<long>(EnrichmentCountSql, Token, gameId));
        Assert.Equal(product.Id, await _database.ScalarAsync<string>(CacheProductIdSql, Token, titleId));
        Assert.Equal(gameId, await _database.ScalarAsync<Guid>(CacheGameIdSql, Token, titleId));
    }

    [Fact]
    public async Task AdmitAsync_ForATitleALibraryRefreshAlreadyCatalogued_ReusesThatGameRatherThanDuplicating()
    {
        var title = Generated.NewCanonicalTitle();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var existing = await InsertExistingGameAsync(title);
        _createdTitleIds.Add(titleId);

        var created = await new StoreCatalogCrawlRepository(_database.DataSource)
            .AdmitAsync([FullGame(title, Generated.NewCoverImageAddress(), titleId)], Token);

        Assert.Equal(0, created);
        Assert.Equal(1L, await _database.ScalarAsync<long>(GameCountByNormalizedTitleSql, Token, NormalizedTitle(title)));
        Assert.Equal(existing, await _database.ScalarAsync<Guid>(CacheGameIdSql, Token, titleId));
    }

    [Fact]
    public async Task AdmitAsync_FillsTheCoverOnAGameThatHadNone_BecauseALibraryRefreshAdmitsWithoutArt()
    {
        var title = Generated.NewCanonicalTitle();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var existing = await InsertExistingGameAsync(title);
        _createdTitleIds.Add(titleId);
        var cover = Generated.NewCoverImageAddress();
        Assert.True(await _database.ScalarAsync<bool>(CoverIsNullSql, Token, existing));

        await new StoreCatalogCrawlRepository(_database.DataSource)
            .AdmitAsync([FullGame(title, cover, titleId)], Token);

        Assert.Equal(cover, await _database.ScalarAsync<string>(CoverSql, Token, existing));
    }

    [Fact]
    public async Task AdmitAsync_LeavesACoverThatIsAlreadyThere_SoASecondWalkDoesNotOverwriteIt()
    {
        var title = Generated.NewCanonicalTitle();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var walkedCover = Generated.NewCoverImageAddress();
        var repository = new StoreCatalogCrawlRepository(_database.DataSource);
        await repository.AdmitAsync([FullGame(title, walkedCover, titleId)], Token);

        await repository.AdmitAsync([FullGame(title, Generated.NewCoverImageAddress(), titleId)], Token);

        var gameId = await TrackAsync(title, titleId);
        Assert.Equal(walkedCover, await _database.ScalarAsync<string>(CoverSql, Token, gameId));
    }

    private static StoreCategoryProduct FullGame(string title, string cover, string titleId) =>
        new()
        {
            Id = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix),
            Name = title,
            NpTitleId = titleId,
            Classification = StoreCategoryProduct.FullGameClassification,
            Media =
            [
                new StoreMediaEntry
                {
                    Role = StoreCoverArt.RolePreference[0],
                    Type = StoreCoverArt.ImageMediaType,
                    Url = cover,
                },
            ],
        };

    private static string NormalizedTitle(string title) =>
        CanonicalizationService.NormalizeName(title) is { } normalized
            ? normalized.Trim().ToLowerInvariant()
            : throw new InvalidOperationException($"A generated canonical title must normalize: {title}");

    private async Task<Guid> InsertExistingGameAsync(string title)
    {
        var gameId = await _database.ScalarAsync<Guid>(
            InsertGameSql, Token, title, NormalizedTitle(title), ContentKinds.Game);
        _createdGames.Add(gameId);
        return gameId;
    }

    private async Task<Guid> TrackAsync(string title, string titleId)
    {
        var gameId = await _database.ScalarAsync<Guid>(GameIdByNormalizedTitleSql, Token, NormalizedTitle(title));
        _createdGames.Add(gameId);
        _createdTitleIds.Add(titleId);
        return gameId;
    }
}
