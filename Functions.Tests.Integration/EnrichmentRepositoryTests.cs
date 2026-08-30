namespace Functions.Tests.Integration;

using Functions.Curator.Enrichment;
using Functions.Curator.Rawg;

[Trait("Category", "Integration")]
[Collection(nameof(CuratorDatabaseCollection))]
public sealed class EnrichmentRepositoryTests : IAsyncLifetime
{
    private const string InsertGameSql =
        "INSERT INTO games (game_id, canonical_title, normalized_title) VALUES ($1, $2, $3)";

    private const string InsertOpenCriticSql =
        "INSERT INTO opencritic_cache (oc_game_id, name, top_critic_score, tier, percent_recommended) VALUES ($1, $2, $3, $4, $5)";

    private const string InsertPublisherTierSql =
        "INSERT INTO publisher_tiers (tier_id, pattern, tier, match_kind) VALUES ($1, $2, $3, $4)";

    private const string RawgNestedSql =
        "SELECT raw -> 'nested' ->> 'kept' FROM rawg_cache WHERE normalized_title = $1";

    private const string RawgRawIsNullSql =
        "SELECT raw IS NULL FROM rawg_cache WHERE normalized_title = $1";

    private const string RawgRowCountSql =
        "SELECT count(*) FROM rawg_cache WHERE normalized_title = $1";

    private const string RawgGameIdSql =
        "SELECT rawg_game_id FROM rawg_cache WHERE normalized_title = $1";

    private const string ActiveGenreCountSql =
        "SELECT count(*) FROM genres WHERE active = true";

    private const string SeededGenreIdSql =
        "SELECT genre_id FROM genres WHERE active = true ORDER BY name LIMIT 1";

    private const string EnrichmentTierSql =
        "SELECT aaa_tier FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentScoreSourceSql =
        "SELECT score_source FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentCriticalScoreSql =
        "SELECT critical_score FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentDeveloperSql =
        "SELECT developer FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentRawgAttemptedAtSql =
        "SELECT rawg_attempted_at FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentPsnEnrichedSql =
        "SELECT psn_enriched FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentPsnAttemptedAtSql =
        "SELECT psn_attempted_at FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentOpenCriticAttemptedAtSql =
        "SELECT opencritic_attempted_at FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentPsnRatingSql =
        "SELECT psn_rating FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentPublisherSql =
        "SELECT publisher FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentRowCountSql =
        "SELECT count(*) FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentRawgEnrichedSql =
        "SELECT rawg_enriched FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentOpenCriticEnrichedSql =
        "SELECT opencritic_enriched FROM game_enrichment WHERE game_id = $1";

    private const string EnrichmentOcScoreSql =
        "SELECT oc_score FROM game_enrichment WHERE game_id = $1";

    private const string FingerprintSql =
        "SELECT rules_fingerprint FROM curation_rule_pass_state WHERE pass_name = $1";

    private const string DeleteEnrichmentSql = "DELETE FROM game_enrichment WHERE game_id = $1";
    private const string DeleteGameSql = "DELETE FROM games WHERE game_id = $1";
    private const string DeleteRawgSql = "DELETE FROM rawg_cache WHERE normalized_title = $1";
    private const string DeletePsnCacheSql = "DELETE FROM psn_catalog_cache WHERE title_id = $1";
    private const string DeleteOpenCriticSql = "DELETE FROM opencritic_cache WHERE oc_game_id = $1";
    private const string DeletePassStateSql = "DELETE FROM curation_rule_pass_state WHERE pass_name = $1";
    private const string DeletePublisherTierSql = "DELETE FROM publisher_tiers WHERE tier_id = $1";

    private readonly CuratorDatabase _database;
    private readonly List<Guid> _createdGames = [];
    private readonly List<string> _createdRawgKeys = [];
    private readonly List<string> _createdTitleIds = [];
    private readonly List<int> _createdOpenCriticIds = [];
    private readonly List<string> _createdPassNames = [];
    private readonly List<Guid> _createdTierIds = [];
    private Guid _identitySub;

    public EnrichmentRepositoryTests(CuratorDatabase database) => _database = database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _identitySub = await _database.CreateUserAsync(Token);

    public async ValueTask DisposeAsync()
    {
        await DeleteRowsCascadingFromTheUserAsync();
        await DeleteGameEnrichmentBeforeGamesAsync();
        await DeleteRowsKeyedIndependentlyOfGamesAsync();
        await DeleteGamesAsync();
    }

    [Fact]
    public async Task SaveRawgCacheAsync_StoresRawAsQueryableJsonbRatherThanText()
    {
        // Arrange
        var title = NewTitle();
        var rawgGameId = Random.Shared.Next(1, 1_000_000);
        var repository = new EnrichmentRepository(_database.DataSource);
        TrackRawg(title);

        // Act
        await repository.SaveRawgCacheAsync(title, rawgGameId, """{"nested":{"kept":"yes"}}""", Token);

        // Assert
        var nested = await _database.ScalarAsync<string>(RawgNestedSql, Token, RawgMatcher.Normalize(title));

        Assert.Equal("yes", nested);
    }

    [Fact]
    public async Task SaveRawgCacheAsync_ForTheSameNormalizedTitle_UpdatesTheRowRatherThanInsertingASecond()
    {
        // Arrange
        var title = NewTitle();
        var firstRawgGameId = Random.Shared.Next(1, 500_000);
        var secondRawgGameId = Random.Shared.Next(500_001, 1_000_000);
        var repository = new EnrichmentRepository(_database.DataSource);
        TrackRawg(title);
        await repository.SaveRawgCacheAsync(title, firstRawgGameId, """{"v":1}""", Token);

        // Act
        await repository.SaveRawgCacheAsync(title, secondRawgGameId, """{"v":2}""", Token);

        // Assert
        var rows = await _database.ScalarAsync<long>(RawgRowCountSql, Token, RawgMatcher.Normalize(title));
        var storedRawgGameId = await _database.ScalarAsync<int>(RawgGameIdSql, Token, RawgMatcher.Normalize(title));

        Assert.Equal(1L, rows);
        Assert.Equal(secondRawgGameId, storedRawgGameId);
    }

    [Fact]
    public async Task SaveRawgCacheAsync_WithNoRaw_StoresSqlNullRatherThanTheStringNull()
    {
        // Arrange
        var title = NewTitle();
        var repository = new EnrichmentRepository(_database.DataSource);
        TrackRawg(title);

        // Act
        await repository.SaveRawgCacheAsync(title, null, null, Token);

        // Assert
        var isNull = await _database.ScalarAsync<bool>(RawgRawIsNullSql, Token, RawgMatcher.Normalize(title));

        Assert.True(isNull);
    }

    [Fact]
    public async Task GetRawgCacheAsync_ReadsBackAnEntryStoredUnderATypographicApostrophe()
    {
        // Arrange
        var stored = NewTitle("Marvel’s Cache");
        var lookup = stored.Replace('’', '\'');
        var rawgGameId = Random.Shared.Next(1, 1_000_000);
        var repository = new EnrichmentRepository(_database.DataSource);
        TrackRawg(stored);
        await repository.SaveRawgCacheAsync(stored, rawgGameId, """{"v":1}""", Token);

        // Act
        var entry = await repository.GetRawgCacheAsync(lookup, Token);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(rawgGameId, entry.RawgGameId);
    }

    [Fact]
    public async Task GetRawgCacheAsync_WhenNothingIsCached_ReturnsNull()
    {
        // Arrange
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        var entry = await repository.GetRawgCacheAsync(NewTitle(), Token);

        // Assert
        Assert.Null(entry);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_RoundTripsTheGenreArrayNumericRatingAndDate()
    {
        // Arrange
        var titleId = NewTitleId();
        var releaseDate = new DateOnly(2000, 1, 1).AddDays(Random.Shared.Next(0, 9_000));
        var conceptId = Guid.NewGuid().ToString();
        var genres = new[] { $"Genre-{Guid.NewGuid():N}", $"Genre-{Guid.NewGuid():N}" };
        var starRating = Random.Shared.Next(0, 10) / 2.0;
        var publisher = $"Publisher-{Guid.NewGuid():N}";
        var contentRating = $"Rating-{Guid.NewGuid():N}";
        var ratingAuthority = $"Authority-{Guid.NewGuid():N}";
        var repository = new EnrichmentRepository(_database.DataSource);
        TrackPsnCache(titleId);
        var entry = new PsnCatalogCacheEntry(
            titleId,
            conceptId,
            genres,
            starRating,
            publisher,
            releaseDate,
            "https://example.invalid/cover.png",
            contentRating,
            ratingAuthority,
            true);

        // Act
        await repository.SavePsnCatalogCacheAsync(entry, Token);

        // Assert
        var stored = await repository.GetPsnCatalogCacheAsync(titleId, Token);

        Assert.NotNull(stored);
        Assert.Equal(genres, stored.Genres);
        Assert.Equal(starRating, stored.StarRating);
        Assert.Equal(releaseDate, stored.ReleaseDate);
        Assert.Equal(contentRating, stored.ContentRating);
        Assert.True(stored.Multiplayer);
        Assert.NotNull(stored.ConceptFetchedAt);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_WhenARefreshOmitsTheCoverImage_KeepsTheStoredOne()
    {
        // Arrange
        var titleId = NewTitleId();
        var conceptId = Guid.NewGuid().ToString();
        var publisher = $"Publisher-{Guid.NewGuid():N}";
        var originalCoverImageUrl = $"https://example.invalid/{Guid.NewGuid():N}.png";
        var originalStarRating = Random.Shared.Next(0, 10) / 2.0;
        var refreshedStarRating = Random.Shared.Next(0, 10) / 2.0;
        var repository = new EnrichmentRepository(_database.DataSource);
        TrackPsnCache(titleId);
        var original = new PsnCatalogCacheEntry(
            titleId, conceptId, [], originalStarRating, publisher, null, originalCoverImageUrl);
        var refreshed = new PsnCatalogCacheEntry(
            titleId, conceptId, [], refreshedStarRating, publisher, null, null);
        await repository.SavePsnCatalogCacheAsync(original, Token);

        // Act
        await repository.SavePsnCatalogCacheAsync(refreshed, Token);

        // Assert
        var stored = await repository.GetPsnCatalogCacheAsync(titleId, Token);

        Assert.NotNull(stored);
        Assert.Equal(originalCoverImageUrl, stored.CoverImageUrl);
        Assert.Equal(refreshedStarRating, stored.StarRating);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_WhenARefreshSuppliesANewCoverImage_OverwritesTheStoredOne()
    {
        // Arrange
        var titleId = NewTitleId();
        var conceptId = Guid.NewGuid().ToString();
        var publisher = $"Publisher-{Guid.NewGuid():N}";
        var starRating = Random.Shared.Next(0, 10) / 2.0;
        var originalCoverImageUrl = $"https://example.invalid/{Guid.NewGuid():N}.png";
        var refreshedCoverImageUrl = $"https://example.invalid/{Guid.NewGuid():N}.png";
        var repository = new EnrichmentRepository(_database.DataSource);
        TrackPsnCache(titleId);
        var original = new PsnCatalogCacheEntry(
            titleId, conceptId, [], starRating, publisher, null, originalCoverImageUrl);
        var refreshed = new PsnCatalogCacheEntry(
            titleId, conceptId, [], starRating, publisher, null, refreshedCoverImageUrl);
        await repository.SavePsnCatalogCacheAsync(original, Token);

        // Act
        await repository.SavePsnCatalogCacheAsync(refreshed, Token);

        // Assert
        var stored = await repository.GetPsnCatalogCacheAsync(titleId, Token);

        Assert.NotNull(stored);
        Assert.Equal(refreshedCoverImageUrl, stored.CoverImageUrl);
    }

    [Fact]
    public async Task GetPsnCatalogCacheAsync_WhenNothingIsCached_ReturnsNull()
    {
        // Arrange
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        var stored = await repository.GetPsnCatalogCacheAsync(NewTitleId(), Token);

        // Assert
        Assert.Null(stored);
    }

    [Fact]
    public async Task GetEnrichmentNeedsAsync_UnnestsTheUuidArrayAndExcludesOnlyGamesWithNothingLeftToFetch()
    {
        // Arrange
        var neverAttempted = await CreateGameAsync();
        var everyProviderAnswered = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        await repository.SaveGameEnrichmentAsync(
            everyProviderAnswered,
            null,
            null,
            MinimalSignals() with { RawgEnriched = true, OpencriticEnriched = true, PsnEnriched = true },
            Token);

        // Act
        var result = await repository.GetEnrichmentNeedsAsync([neverAttempted, everyProviderAnswered], Token);

        // Assert
        var need = Assert.Single(result);
        Assert.Equal(neverAttempted, need.GameId);
        Assert.True(need.Rawg);
        Assert.True(need.OpenCritic);
        Assert.True(need.Psn);
    }

    [Fact]
    public async Task GetEnrichmentNeedsAsync_StillReturnsAGameWhosePsnLegNeverSucceeded_AndAsksForPsnAlone()
    {
        // Arrange
        var rawgOnly = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var everythingButPsn = MinimalSignals() with
        {
            RawgEnriched = true,
            RawgAttempted = true,
            OpencriticEnriched = true,
            PsnEnriched = false,
            PsnAttempted = true,
        };
        await repository.SaveGameEnrichmentAsync(rawgOnly, null, null, everythingButPsn, Token);

        // Act
        var result = await repository.GetEnrichmentNeedsAsync([rawgOnly], Token);

        // Assert
        const string reason =
            "Retry is driven by the per-provider success flag, never by an attempt timestamp: this row records "
            + "psn_attempted_at yet psn_enriched is still false, so PS Store has to be asked again. A library "
            + "refresh is the only moment the user's PSN session is available, and PS Store is the source of "
            + "truth for genre, so retiring the game because RAWG happened to answer is what left 874 of 886 "
            + "owned titles permanently without a genre. Equally, RAWG and OpenCritic already answered and "
            + "must not be asked a second time.";
        var need = Assert.Single(result);
        Assert.Equal(rawgOnly, need.GameId);
        Assert.True(need.Psn, reason);
        Assert.False(need.Rawg, reason);
        Assert.False(need.OpenCritic, reason);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_ForAPassThatConsultedNobody_KeepsEveryFlagAndValueAnEarlierPassEarned()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var openCriticScore = Math.Round(Random.Shared.NextDouble() * 100, 2);
        var everyProviderAnswered = MinimalSignals() with
        {
            RawgEnriched = true,
            RawgAttempted = true,
            OpencriticEnriched = true,
            OpencriticAttempted = true,
            PsnEnriched = true,
            PsnAttempted = true,
            OcScore = openCriticScore,
        };
        await repository.SaveGameEnrichmentAsync(gameId, null, null, everyProviderAnswered, Token);

        // Act
        await repository.SaveGameEnrichmentAsync(gameId, null, null, MinimalSignals(), Token);

        // Assert
        const string reason =
            "A pass that consulted no provider must not write its own emptiness over what earlier passes "
            + "earned. Unguarded, `rawg_enriched = EXCLUDED.rawg_enriched` took 1018 rows to 481 on the local "
            + "database in a single catalog pass, and the same shape erased oc_score whenever OpenCritic "
            + "stayed silent.";
        Assert.True(
            await _database.ScalarAsync<bool>(EnrichmentRawgEnrichedSql, Token, Guid.Parse(gameId)),
            reason);
        Assert.True(
            await _database.ScalarAsync<bool>(EnrichmentOpenCriticEnrichedSql, Token, Guid.Parse(gameId)),
            reason);
        Assert.True(
            await _database.ScalarAsync<bool>(EnrichmentPsnEnrichedSql, Token, Guid.Parse(gameId)),
            reason);
        Assert.Equal(
            (decimal)openCriticScore,
            await _database.ScalarAsync<decimal>(EnrichmentOcScoreSql, Token, Guid.Parse(gameId)));
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_RecordsProviderAttemptTimestampsWithoutRetiringTheGame()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId,
            null,
            null,
            MinimalSignals() with { PsnAttempted = true, OpencriticAttempted = true },
            Token);
        var psnAttemptedAt = await _database.ScalarAsync<DateTime>(EnrichmentPsnAttemptedAtSql, Token, Guid.Parse(gameId));
        var openCriticAttemptedAt =
            await _database.ScalarAsync<DateTime>(EnrichmentOpenCriticAttemptedAtSql, Token, Guid.Parse(gameId));
        var stillACandidate = await repository.GetEnrichmentNeedsAsync([gameId], Token);

        // Assert
        const string reason =
            "The timestamps answer 'when did we last try', which is operational history. They must not answer "
            + "'should we try again' -- that belongs to the success flags, which are all still false here.";
        Assert.NotEqual(default, psnAttemptedAt);
        Assert.NotEqual(default, openCriticAttemptedAt);
        Assert.True(stillACandidate.Any(need => need.GameId == gameId), reason);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_StoresTheScoresAndSatisfiesTheTierAndScoreSourceChecks()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var releaseYear = Random.Shared.Next(1990, 2030);
        var developer = $"Developer-{Guid.NewGuid():N}";
        var publisher = $"Publisher-{Guid.NewGuid():N}";
        var esrb = $"Esrb-{Guid.NewGuid():N}";
        var criticalScore = Random.Shared.Next(0, 200) / 2.0;
        var ocScore = Random.Shared.Next(0, 20_000) / 200.0;
        var ocTier = $"Tier-{Guid.NewGuid():N}";
        var ocPercentRecommended = Random.Shared.Next(0, 20_000) / 200.0;
        var psnRating = Random.Shared.Next(0, 10) / 2.0;
        var signals = new GameEnrichmentSignals(
            releaseYear,
            developer,
            publisher,
            esrb,
            true,
            criticalScore,
            ocScore,
            ocTier,
            ocPercentRecommended,
            psnRating,
            EnrichmentOrchestrationService.RawgAndOpenCriticScoreSource,
            PublisherTierRuleSet.AaaTier,
            true,
            true);

        // Act
        await repository.SaveGameEnrichmentAsync(gameId, null, null, signals, Token);

        // Assert
        var tier = await _database.ScalarAsync<string>(EnrichmentTierSql, Token, Guid.Parse(gameId));
        var scoreSource = await _database.ScalarAsync<string>(EnrichmentScoreSourceSql, Token, Guid.Parse(gameId));
        var storedCriticalScore = await _database.ScalarAsync<decimal>(
            EnrichmentCriticalScoreSql, Token, Guid.Parse(gameId));

        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
        Assert.Equal(EnrichmentOrchestrationService.RawgAndOpenCriticScoreSource, scoreSource);
        Assert.Equal((decimal)criticalScore, storedCriticalScore);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_StoresPsnEnriched_ForAConceptThatCarriedNoStarRating()
    {
        // Arrange
        var resolvedGameId = await CreateGameAsync();
        var unresolvedGameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var conceptWithoutARating = MinimalSignals() with { PsnEnriched = true, PsnRating = null };
        var ratingWithoutAConcept = MinimalSignals() with
        {
            PsnEnriched = false,
            PsnRating = Random.Shared.Next(0, 10) / 2.0,
        };

        // Act
        await repository.SaveGameEnrichmentAsync(resolvedGameId, null, null, conceptWithoutARating, Token);
        await repository.SaveGameEnrichmentAsync(unresolvedGameId, null, null, ratingWithoutAConcept, Token);

        // Assert
        var resolved = await _database.ScalarAsync<bool>(
            EnrichmentPsnEnrichedSql, Token, Guid.Parse(resolvedGameId));
        var unresolved = await _database.ScalarAsync<bool>(
            EnrichmentPsnEnrichedSql, Token, Guid.Parse(unresolvedGameId));

        Assert.True(resolved);
        Assert.False(unresolved);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_KeepsPsnSourcedValues_WhenALaterPassNeverReachedPsn()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var psnRating = Random.Shared.Next(0, 10) / 2.0;
        var psnAnswered = MinimalSignals() with { PsnEnriched = true, PsnRating = psnRating };
        var psnNeverConsulted = MinimalSignals() with { PsnEnriched = false, PsnRating = null };

        // Act
        await repository.SaveGameEnrichmentAsync(gameId, null, null, psnAnswered, Token);
        await repository.SaveGameEnrichmentAsync(gameId, null, null, psnNeverConsulted, Token);

        // Assert
        var storedPsnEnriched = await _database.ScalarAsync<bool>(
            EnrichmentPsnEnrichedSql, Token, Guid.Parse(gameId));
        var storedPsnRating = await _database.ScalarAsync<decimal>(
            EnrichmentPsnRatingSql, Token, Guid.Parse(gameId));

        Assert.True(storedPsnEnriched);
        Assert.Equal((decimal)psnRating, storedPsnRating);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_KeepsRawgSourcedValues_WhenALaterPassNeverReachedRawg()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var developer = $"Developer-{Guid.NewGuid():N}";
        var criticalScore = Random.Shared.Next(0, 200) / 2.0;
        var enriched = MinimalSignals() with
        {
            Developer = developer,
            CriticalScore = criticalScore,
            ScoreSource = EnrichmentOrchestrationService.RawgOnlyScoreSource,
            RawgEnriched = true,
            RawgAttempted = true,
        };

        // Act
        await repository.SaveGameEnrichmentAsync(gameId, null, null, enriched, Token);
        await repository.SaveGameEnrichmentAsync(gameId, null, null, MinimalSignals(), Token);

        // Assert
        var storedDeveloper = await _database.ScalarAsync<string>(EnrichmentDeveloperSql, Token, Guid.Parse(gameId));
        var storedCriticalScore = await _database.ScalarAsync<decimal>(
            EnrichmentCriticalScoreSql, Token, Guid.Parse(gameId));
        var storedScoreSource = await _database.ScalarAsync<string>(
            EnrichmentScoreSourceSql, Token, Guid.Parse(gameId));

        Assert.Equal(developer, storedDeveloper);
        Assert.Equal((decimal)criticalScore, storedCriticalScore);
        Assert.Equal(EnrichmentOrchestrationService.RawgOnlyScoreSource, storedScoreSource);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_KeepsAnEarlierRawgAttemptStamp_WhenALaterPassNeverReachedRawg()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId, null, null, MinimalSignals() with { RawgAttempted = true }, Token);
        var afterAttempt = await _database.ScalarAsync<DateTime>(
            EnrichmentRawgAttemptedAtSql, Token, Guid.Parse(gameId));
        await repository.SaveGameEnrichmentAsync(gameId, null, null, MinimalSignals(), Token);
        var afterNeverAsked = await _database.ScalarAsync<DateTime>(
            EnrichmentRawgAttemptedAtSql, Token, Guid.Parse(gameId));

        // Assert
        Assert.Equal(afterAttempt, afterNeverAsked);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_ForAGameAlreadyEnriched_UpdatesTheRowInPlace()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var firstPublisher = $"Publisher-{Guid.NewGuid():N}";
        var secondPublisher = $"Publisher-{Guid.NewGuid():N}";
        var first = MinimalSignals() with { Publisher = firstPublisher };
        var second = MinimalSignals() with { Publisher = secondPublisher };
        await repository.SaveGameEnrichmentAsync(gameId, null, null, first, Token);

        // Act
        await repository.SaveGameEnrichmentAsync(gameId, null, null, second, Token);

        // Assert
        var rows = await _database.ScalarAsync<long>(EnrichmentRowCountSql, Token, Guid.Parse(gameId));
        var storedPublisher = await _database.ScalarAsync<string>(EnrichmentPublisherSql, Token, Guid.Parse(gameId));

        Assert.Equal(1L, rows);
        Assert.Equal(secondPublisher, storedPublisher);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_WithASeededGenreId_SatisfiesTheGenreForeignKey()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var genreId = await _database.ScalarAsync<Guid>(SeededGenreIdSql, Token);
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        await repository.SaveGameEnrichmentAsync(gameId, genreId.ToString(), null, MinimalSignals(), Token);

        // Assert
        var rows = await _database.ScalarAsync<long>(EnrichmentRowCountSql, Token, Guid.Parse(gameId));

        Assert.Equal(1L, rows);
    }

    [Fact]
    public async Task GetActiveGenresAsync_ReturnsEverySeededActiveGenre()
    {
        // Arrange
        var repository = new EnrichmentRepository(_database.DataSource);
        var expected = await _database.ScalarAsync<long>(ActiveGenreCountSql, Token);

        // Act
        var genres = await repository.GetActiveGenresAsync(Token);

        // Assert
        Assert.Equal(expected, genres.Count);
    }

    [Fact]
    public async Task GetAllOpenCriticGamesAsync_ReadsNumericColumnsAsDoubles()
    {
        // Arrange
        var topCriticScore = Random.Shared.Next(0, 400) / 4m;
        var percentRecommended = Random.Shared.Next(0, 400) / 4m;
        var tier = $"Tier-{Guid.NewGuid():N}";
        var ocGameId = await CreateOpenCriticGameAsync(topCriticScore, tier, percentRecommended);
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        var games = await repository.GetAllOpenCriticGamesAsync(Token);

        // Assert
        var stored = Assert.Single(games, game => game.OcGameId == ocGameId);

        Assert.Equal((double)topCriticScore, stored.TopCriticScore);
        Assert.Equal((double)percentRecommended, stored.PercentRecommended);
        Assert.Equal(tier, stored.Tier);
    }

    [Fact]
    public async Task GetAllOpenCriticGamesAsync_WhenTheTierColumnIsNull_KeepsItNullRatherThanEmpty()
    {
        // Arrange
        var ocGameId = await CreateOpenCriticGameAsync(null, null, null);
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        var games = await repository.GetAllOpenCriticGamesAsync(Token);

        // Assert
        var stored = Assert.Single(games, game => game.OcGameId == ocGameId);

        Assert.Null(stored.Tier);
        Assert.Null(stored.TopCriticScore);
    }

    [Fact]
    public async Task ListPublisherTierRulesAsync_ReadsARuleThatSatisfiesTheTierAndMatchKindChecks()
    {
        // Arrange
        var tierId = await CreatePublisherTierAsync(
            CuratorDatabase.TestPublisherTierPattern, PublisherTierRuleSet.AaTier, PublisherTierRuleSet.SubstringMatchKind);
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        var rules = await repository.ListPublisherTierRulesAsync(Token);

        // Assert
        var stored = Assert.Single(rules, rule => rule.TierId == tierId);

        Assert.Equal(PublisherTierRuleSet.AaTier, stored.Tier);
        Assert.Equal(PublisherTierRuleSet.SubstringMatchKind, stored.MatchKind);
    }

    [Fact]
    public async Task SetPublisherTierRulesFingerprintAsync_RoundTripsAndOverwritesOnASecondWrite()
    {
        // Arrange
        var repository = new EnrichmentRepository(_database.DataSource);
        TrackPassState(CurationPassNames.TierReclassification);
        var firstFingerprint = Guid.NewGuid().ToString();
        var secondFingerprint = Guid.NewGuid().ToString();
        await repository.SetPublisherTierRulesFingerprintAsync(firstFingerprint, Token);

        // Act
        await repository.SetPublisherTierRulesFingerprintAsync(secondFingerprint, Token);

        // Assert
        var read = await repository.GetPublisherTierRulesFingerprintAsync(Token);
        var stored = await _database.ScalarAsync<string>(FingerprintSql, Token, CurationPassNames.TierReclassification);

        Assert.Equal(secondFingerprint, read);
        Assert.Equal(secondFingerprint, stored);
    }

    [Fact]
    public async Task GetPublisherTierRulesFingerprintAsync_WhenThePassHasNeverRun_ReturnsNull()
    {
        // Arrange
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        var fingerprint = await repository.GetPublisherTierRulesFingerprintAsync(Token);

        // Assert
        Assert.Null(fingerprint);
    }

    [Fact]
    public async Task ReclassifyTierAsync_RewritesTheStoredTierFromThePreparedRules()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var signals = MinimalSignals() with
        {
            Publisher = "Integration Test Studio",
            AaaTier = PublisherTierRuleSet.IndieTier,
        };
        await repository.SaveGameEnrichmentAsync(gameId, null, null, signals, Token);
        var tierId = Guid.NewGuid();
        var rules = new List<PublisherTierRule>
        {
            new(tierId, "integration test studio", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.ExactMatchKind),
        };

        // Act
        var updated = await repository.ReclassifyTierAsync(rules, Token);

        // Assert
        var tier = await _database.ScalarAsync<string>(EnrichmentTierSql, Token, Guid.Parse(gameId));

        Assert.Equal(1, updated);
        Assert.Equal(PublisherTierRuleSet.AaaTier, tier);
    }

    [Fact]
    public async Task ReclassifyTierAsync_WhenTheStoredTierAlreadyMatches_ReportsNothingUpdated()
    {
        // Arrange
        var gameId = await CreateGameAsync();
        var repository = new EnrichmentRepository(_database.DataSource);
        var signals = MinimalSignals() with
        {
            Publisher = "Integration Test Studio",
            AaaTier = PublisherTierRuleSet.IndieTier,
        };
        await repository.SaveGameEnrichmentAsync(gameId, null, null, signals, Token);
        var tierId = Guid.NewGuid();
        var rules = new List<PublisherTierRule>
        {
            new(tierId, "integration test studio", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.ExactMatchKind),
        };
        await repository.ReclassifyTierAsync(rules, Token);

        // Act
        var updated = await repository.ReclassifyTierAsync(rules, Token);

        // Assert
        Assert.Equal(0, updated);
    }

    [Fact]
    public async Task TryLockCatalogEnrichmentPassAsync_WhileTheLockIsHeld_ReportsContended()
    {
        // Arrange
        var repository = new EnrichmentRepository(_database.DataSource);

        // Act
        await using var held = await repository.TryLockCatalogEnrichmentPassAsync(Token);
        await using var contended = await repository.TryLockCatalogEnrichmentPassAsync(Token);

        // Assert
        Assert.True(held.Acquired);
        Assert.False(contended.Acquired);
    }

    [Fact]
    public async Task TryLockCatalogEnrichmentPassAsync_AfterTheFirstHolderReleases_AcquiresAgain()
    {
        // Arrange
        var repository = new EnrichmentRepository(_database.DataSource);
        var first = await repository.TryLockCatalogEnrichmentPassAsync(Token);
        await first.DisposeAsync();

        // Act
        await using var second = await repository.TryLockCatalogEnrichmentPassAsync(Token);

        // Assert
        Assert.True(first.ReleasedBeforeClosing);
        Assert.True(second.Acquired);
    }

    private static GameEnrichmentSignals MinimalSignals() =>
        new(null, null, null, null, null, null, null, null, null, null, null, null, false, false);

    private static string NewTitle() => Guid.NewGuid().ToString();

    private static string NewTitle(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private static string NewTitleId() => $"CUSA{Guid.NewGuid():N}";

    private void TrackRawg(string title) => _createdRawgKeys.Add(RawgMatcher.Normalize(title));

    private void TrackPsnCache(string titleId) => _createdTitleIds.Add(titleId);

    private void TrackPassState(string passName) => _createdPassNames.Add(passName);

    private async Task DeleteRowsCascadingFromTheUserAsync() =>
        await _database.DeleteUserAsync(_identitySub, Token);

    private async Task DeleteGameEnrichmentBeforeGamesAsync()
    {
        foreach (var gameId in _createdGames)
        {
            await _database.ExecuteAsync(DeleteEnrichmentSql, Token, gameId);
        }
    }

    private async Task DeleteRowsKeyedIndependentlyOfGamesAsync()
    {
        foreach (var key in _createdRawgKeys)
        {
            await _database.ExecuteAsync(DeleteRawgSql, Token, key);
        }

        foreach (var titleId in _createdTitleIds)
        {
            await _database.ExecuteAsync(DeletePsnCacheSql, Token, titleId);
        }

        foreach (var ocId in _createdOpenCriticIds)
        {
            await _database.ExecuteAsync(DeleteOpenCriticSql, Token, ocId);
        }

        foreach (var passName in _createdPassNames)
        {
            await _database.ExecuteAsync(DeletePassStateSql, Token, passName);
        }

        foreach (var tierId in _createdTierIds)
        {
            await _database.ExecuteAsync(DeletePublisherTierSql, Token, tierId);
        }
    }

    private async Task DeleteGamesAsync()
    {
        foreach (var gameId in _createdGames)
        {
            await _database.ExecuteAsync(DeleteGameSql, Token, gameId);
        }
    }

    private async Task<string> CreateGameAsync()
    {
        var gameId = Guid.NewGuid();
        var title = NewTitle();
        await _database.ExecuteAsync(InsertGameSql, Token, gameId, title, title.ToLowerInvariant());
        _createdGames.Add(gameId);
        return gameId.ToString();
    }

    private async Task<int> CreateOpenCriticGameAsync(
        decimal? topCriticScore,
        string? tier,
        decimal? percentRecommended)
    {
        var ocGameId = Random.Shared.Next(1_000_000, 2_000_000);
        await _database.ExecuteAsync(
            InsertOpenCriticSql,
            Token,
            ocGameId,
            NewTitle(),
            (object?)topCriticScore ?? DBNull.Value,
            (object?)tier ?? DBNull.Value,
            (object?)percentRecommended ?? DBNull.Value);
        _createdOpenCriticIds.Add(ocGameId);
        return ocGameId;
    }

    private async Task<Guid> CreatePublisherTierAsync(string pattern, string tier, string matchKind)
    {
        var tierId = Guid.NewGuid();
        await _database.ExecuteAsync(InsertPublisherTierSql, Token, tierId, pattern, tier, matchKind);
        _createdTierIds.Add(tierId);
        return tierId;
    }
}
