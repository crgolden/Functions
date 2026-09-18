namespace Functions.Tests.Unit;

using System.Data;
using System.Text.Json;
using Curator;
using Curator.Catalog;
using Curator.Enrichment;
using Curator.OpenCritic;
using Curator.Store;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentRepositoryTests
{
    [Fact]
    public async Task GetAllOpenCriticGamesAsync_MapsRows_KeepingNullColumnsNullRatherThanEmpty()
    {
        // Arrange
        var gameOneId = TestValues.NewOpenCriticGameId();
        var gameOneName = TestValues.NewGameTitle();
        var topCriticScore = TestValues.NewCriticScore();
        var tier = TestValues.NewOpenCriticTier();
        var percentRecommended = TestValues.NewPercentRecommended();
        var gameTwoId = TestValues.NewOpenCriticGameId();
        var gameTwoName = TestValues.NewGameTitle();
        var table = new DataTable();
        table.Columns.Add("oc_game_id", typeof(int));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("top_critic_score", typeof(double));
        table.Columns.Add("tier", typeof(string));
        table.Columns.Add("percent_recommended", typeof(double));
        table.Rows.Add(gameOneId, gameOneName, topCriticScore, tier, percentRecommended);
        table.Rows.Add(gameTwoId, gameTwoName, DBNull.Value, DBNull.Value, DBNull.Value);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var games = await repository.GetAllOpenCriticGamesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            [
                new OpenCriticGame(gameOneId, gameOneName, topCriticScore, tier, percentRecommended),
                new OpenCriticGame(gameTwoId, gameTwoName, null, null, null),
            ],
            games);
    }

    [Fact]
    public async Task GetRawgCacheAsync_ReturnsNull_WhenNoRow()
    {
        // Arrange
        var missingTitle = TestValues.NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var entry = await repository.GetRawgCacheAsync(missingTitle, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(entry);
    }

    [Fact]
    public async Task GetRawgCacheAsync_NormalizesTheTitleBeforeLookup()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("normalized_title", typeof(string));
        table.Columns.Add("rawg_game_id", typeof(int));
        table.Columns.Add("raw", typeof(string));
        var normalizedTitle = TestValues.NewGameTitle();
        var titleAsTheCallerSpellsIt = $"{normalizedTitle.ToUpperInvariant()}™";
        var rawgGameId = TestValues.NewRawgGameId();
        var raw = JsonSerializer.Serialize(new { id = rawgGameId });
        table.Rows.Add(normalizedTitle, rawgGameId, raw);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var entry = await repository.GetRawgCacheAsync(titleAsTheCallerSpellsIt, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(rawgGameId, entry.RawgGameId);
        Assert.Equal(raw, entry.Raw);
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(normalizedTitle, command.Parameters["@normalized_title"].Value);
    }

    [Fact]
    public async Task SaveRawgCacheAsync_NormalizesTitleAndStoresNullMatch()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var normalizedTitle = TestValues.NewGameTitle();

        // Act
        await repository.SaveRawgCacheAsync(
            normalizedTitle.ToUpperInvariant(),
            null,
            null,
            TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(normalizedTitle, command.Parameters["@normalized_title"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["@rawg_game_id"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["@raw"].Value);
        Assert.Contains("INSERT INTO rawg_cache", command.CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPsnCatalogCacheAsync_ReturnsNull_WhenNoRow()
    {
        // Arrange
        var missingTitleId = TestValues.NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var entry = await repository.GetPsnCatalogCacheAsync(missingTitleId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(entry);
    }

    [Fact]
    public async Task GetPsnCatalogCacheAsync_MapsACompletedLookupRow()
    {
        // Arrange
        var titleId = TestValues.NewTitleId();
        var conceptId = TestValues.NewConceptId();
        var genres = new[] { TestValues.NewGenre(), TestValues.NewGenre() };
        var starRating = TestValues.NewStarRating();
        var publisher = TestValues.NewPublisher();
        var releaseDate = TestValues.NewReleaseDate();
        var coverImageUrl = TestValues.NewCoverImageUrl();
        var contentRating = TestValues.NewContentRating();
        var ratingAuthority = TestValues.NewRatingAuthority();
        var resolvedAt = TestValues.NewUtcTimestamp();
        var table = PsnCatalogCacheTable();
        table.Rows.Add(
            titleId,
            conceptId,
            genres,
            starRating,
            publisher,
            releaseDate,
            coverImageUrl,
            contentRating,
            ratingAuthority,
            true,
            resolvedAt);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var entry = await repository.GetPsnCatalogCacheAsync(titleId, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(entry);
        Assert.Equal(titleId, entry.TitleId);
        Assert.Equal(conceptId, entry.ConceptId);
        Assert.Equal(genres, entry.Genres);
        Assert.Equal(starRating, entry.StarRating);
        Assert.Equal(publisher, entry.Publisher);
        Assert.Equal(releaseDate, entry.ReleaseDate);
        Assert.Equal(coverImageUrl, entry.CoverImageUrl);
        Assert.Equal(contentRating, entry.ContentRating);
        Assert.Equal(ratingAuthority, entry.RatingAuthority);
        Assert.True(entry.Multiplayer);
        Assert.Equal(resolvedAt, entry.ConceptFetchedAt);
    }

    [Fact]
    public async Task GetPsnCatalogCacheAsync_NullConceptFetchedAt_MeansASeededPlaceholderNotACompletedLookup()
    {
        // Arrange
        var titleId = TestValues.NewTitleId();
        var coverImageUrl = TestValues.NewCoverImageUrl();
        var table = PsnCatalogCacheTable();
        table.Rows.Add(
            titleId,
            DBNull.Value,
            Array.Empty<string>(),
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            coverImageUrl,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var entry = await repository.GetPsnCatalogCacheAsync(titleId, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(entry);
        Assert.Null(entry.ConceptFetchedAt);
        Assert.Null(entry.ConceptId);
        Assert.Equal(coverImageUrl, entry.CoverImageUrl);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_StampsConceptFetchedAtOnBothUpsertBranches()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SavePsnCatalogCacheAsync(NewConceptWithoutACoverImage(), TestContext.Current.CancellationToken);

        // Assert
        var sql = Assert.Single(dataSource.ExecutedCommands).ExecutedSql;
        Assert.Collection(
            sql.Split("DO UPDATE SET"),
            insertBranch => Assert.Contains("concept_fetched_at", insertBranch, StringComparison.Ordinal),
            updateBranch => Assert.Contains("concept_fetched_at = now()", updateBranch, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_KeepsASeededCoverImage_WhenTheIncomingConceptCarriesNone()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SavePsnCatalogCacheAsync(NewConceptWithoutACoverImage(), TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Contains(
            "cover_image_url = COALESCE(EXCLUDED.cover_image_url, psn_catalog_cache.cover_image_url)",
            command.ExecutedSql,
            StringComparison.Ordinal);
        Assert.Equal(DBNull.Value, command.Parameters["@cover_image_url"].Value);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_SendsTheGenresArrayAndCoreFields()
    {
        // Arrange
        var titleId = TestValues.NewTitleId();
        var genres = new[] { TestValues.NewGenre() };
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SavePsnCatalogCacheAsync(
            NewConceptWithoutACoverImage(titleId, genres),
            TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(titleId, command.Parameters["@title_id"].Value);
        Assert.Equal(genres, command.Parameters["@genres"].Value);
        Assert.Contains("INSERT INTO psn_catalog_cache", command.CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_SendsTheConceptTypeOnBothUpsertBranches()
    {
        // Arrange
        var conceptType = TestValues.NewToken();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SavePsnCatalogCacheAsync(
            NewConceptWithoutACoverImage() with { ConceptType = conceptType },
            TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(conceptType, command.Parameters["@concept_type"].Value);
        Assert.Contains("concept_type = EXCLUDED.concept_type", command.ExecutedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_ClassifiesTheLinkedGameAsAMediaApp_WhenPsnSaysTheConceptIsAnApplication()
    {
        // Arrange
        var entry = NewConceptWithoutACoverImage() with { ConceptType = ContentKinds.ApplicationConceptType };
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SavePsnCatalogCacheAsync(entry, TestContext.Current.CancellationToken);

        // Assert
        var classify = Assert.Single(
            dataSource.ExecutedCommands,
            command => command.ExecutedSql.Contains("UPDATE games SET content_kind", StringComparison.Ordinal));
        Assert.Equal(ContentKinds.MediaApp, classify.Parameters["@content_kind"].Value);
        Assert.Equal(entry.ConceptId, classify.Parameters["@concept_id"].Value);
        Assert.Contains("FROM game_concepts WHERE concept_id = @concept_id", classify.ExecutedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_LeavesTheGamesTableAlone_WhenTheConceptIsNotAnApplication()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SavePsnCatalogCacheAsync(
            NewConceptWithoutACoverImage() with { ConceptType = TestValues.NewToken() },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            dataSource.ExecutedCommands,
            command => command.ExecutedSql.Contains("UPDATE games", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetEnrichmentNeedsAsync_ReturnsEmpty_WithoutOpeningAConnection_WhenNoCandidateIds()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var unenriched = await repository.GetEnrichmentNeedsAsync([], TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(unenriched);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task GetEnrichmentNeedsAsync_SelectsOnEverySuccessFlag_NeverOnAnAttemptStamp()
    {
        // Arrange
        var unenrichedId = Guid.NewGuid();
        var candidateId = TestValues.NewGameId();
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("needs_rawg", typeof(bool));
        table.Columns.Add("needs_opencritic", typeof(bool));
        table.Columns.Add("needs_psn", typeof(bool));
        table.Rows.Add(unenrichedId, false, true, false);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var unenriched = await repository.GetEnrichmentNeedsAsync(
            [candidateId, unenrichedId],
            TestContext.Current.CancellationToken);

        // Assert
        var need = Assert.Single(unenriched);
        Assert.Equal(unenrichedId, need.GameId);
        Assert.False(need.Rawg);
        Assert.True(need.OpenCritic);
        Assert.False(need.Psn);
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Contains("unnest(@game_ids::uuid[])", command.CapturedCommandText, StringComparison.Ordinal);
        Assert.Contains("NOT game_enrichment.rawg_enriched", command.CapturedCommandText, StringComparison.Ordinal);
        Assert.Contains(
            "NOT game_enrichment.opencritic_enriched", command.CapturedCommandText, StringComparison.Ordinal);
        Assert.Contains("NOT game_enrichment.psn_enriched", command.CapturedCommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "attempted_at",
            command.CapturedCommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetActiveGenresAsync_MapsRows()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("genre_id", typeof(Guid));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("priority", typeof(int));
        var shooterId = Guid.NewGuid();
        var rpgId = Guid.NewGuid();
        var shooterName = TestValues.NewGenre();
        var rpgName = TestValues.NewGenre();
        var shooterPriority = Random.Shared.Next(0, 100);
        var rpgPriority = Random.Shared.Next(0, 100);
        table.Rows.Add(shooterId, shooterName, shooterPriority);
        table.Rows.Add(rpgId, rpgName, rpgPriority);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var genres = await repository.GetActiveGenresAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            [
                new ActiveGenre(shooterId, shooterName, shooterPriority),
                new ActiveGenre(rpgId, rpgName, rpgPriority),
            ],
            genres);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_ExecutesUpsertWithEverySignal()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = TestValues.NewGameId();
        var genreId = Guid.NewGuid();
        var subgenreId = Guid.NewGuid();
        var releaseYear = TestValues.NewReleaseYear();
        var developer = TestValues.NewPublisher();
        var publisher = TestValues.NewPublisher();
        var esrb = TestValues.NewToken();
        var criticalScore = TestValues.NewCriticScore();
        var ocScore = TestValues.NewCriticScore();
        var ocTier = TestValues.NewOpenCriticTier();
        var ocPercentRecommended = TestValues.NewPercentRecommended();
        var psnRating = TestValues.NewStarRating();
        var psnRatingCount = TestValues.NewPsnRatingCount();
        var scoreSource = TestValues.NewToken();
        var aaaTier = TestValues.NewToken();
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
            scoreSource,
            aaaTier,
            true,
            true,
            PsnRatingCount: psnRatingCount);

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId,
            genreId,
            subgenreId,
            signals,
            TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Contains("INSERT INTO game_enrichment", command.CapturedCommandText, StringComparison.Ordinal);
        Assert.Equal(gameId, command.Parameters["@game_id"].Value);
        Assert.Equal(genreId, command.Parameters["@genre_id"].Value);
        Assert.Equal(subgenreId, command.Parameters["@subgenre_id"].Value);
        Assert.Equal(psnRating, command.Parameters["@psn_rating"].Value);
        Assert.Equal(psnRatingCount, command.Parameters["@psn_rating_count"].Value);
        Assert.Contains("psn_rating_count = CASE", command.ExecutedSql, StringComparison.Ordinal);
        Assert.True(command.Parameters["@rawg_enriched"].Value is true);
        Assert.True(command.Parameters["@opencritic_enriched"].Value is true);
    }

    [Fact]
    public async Task GetStoreProductsNeedingPsnEnrichmentAsync_AsksForStoreProductsNotYetEnrichedByPsn_NeverAttemptedFirst()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var title = TestValues.NewGameTitle();
        var titleId = TestValues.NewTitleId();
        var storeProductId = TestValues.NewStoreProductId();
        var limit = Random.Shared.Next(1, 500);
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("canonical_title", typeof(string));
        table.Columns.Add("title_id", typeof(string));
        table.Columns.Add("store_product_id", typeof(string));
        table.Rows.Add(gameId, title, titleId, storeProductId);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var candidates = await repository.GetStoreProductsNeedingPsnEnrichmentAsync(limit, TestContext.Current.CancellationToken);

        // Assert
        var candidate = Assert.Single(candidates);
        Assert.Equal(new StoreProductCandidate(gameId, title, titleId, storeProductId), candidate);
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(limit, command.Parameters["@limit"].Value);
        Assert.Contains("c.store_product_id IS NOT NULL", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(ge.psn_enriched, false) = false", command.ExecutedSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY ge.psn_attempted_at NULLS FIRST", command.ExecutedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryLockStoreProductPassAsync_TakesTheEnrichmentRunLockClassUnderItsOwnKey()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(true));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await using var handle = await repository.TryLockStoreProductPassAsync(TestContext.Current.CancellationToken);

        // Assert
        var command = dataSource.ExecutedCommands[0];
        Assert.True(handle.Acquired);
        Assert.Equal(CuratorAdvisoryLocks.EnrichmentRun, command.Parameters["@lock_class"].Value);
        Assert.Equal(EnrichmentRepository.StoreProductPassLockKey, command.Parameters["@lock_key"].Value);
    }

    [Fact]
    public void PassLockKeys_KeepTheSpellingsAnOperatorLooksUpInPgLocks()
    {
        // Act
        string[] lockKeys = [EnrichmentRepository.CatalogEnrichmentPassLockKey, EnrichmentRepository.StoreProductPassLockKey];

        // Assert
        Assert.Equal(["catalog_enrichment_pass", "store_product_enrichment"], lockKeys);
    }

    [Fact]
    public async Task GetActiveGenresWithLabelsAsync_ReadsTheDisplayNameBesideTheKey()
    {
        // Arrange
        var genreId = Guid.NewGuid();
        var name = TestValues.NewGenre();
        var displayName = TestValues.NewGenreDisplayName();
        var priority = TestValues.NewRulePriority();
        var table = new DataTable();
        table.Columns.Add("genre_id", typeof(Guid));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("display_name", typeof(string));
        table.Columns.Add("priority", typeof(int));
        table.Rows.Add(genreId, name, displayName, priority);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var genres = await repository.GetActiveGenresWithLabelsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([new StoreGenre(genreId, name, displayName, priority)], genres);
        Assert.Contains("WHERE active = true", Assert.Single(dataSource.ExecutedCommands).ExecutedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_WritesPsnEnriched_WhenPsnResolvedTheConceptEvenWithoutAStarRating()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = TestValues.NewGameId();
        var conceptWithoutARating = NoSignals() with { PsnEnriched = true, PsnRating = null };

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId, null, null, conceptWithoutARating, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(dataSource.ExecutedCommands).Parameters["@psn_enriched"].Value is true);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_LeavesPsnEnrichedFalse_WhenAStarRatingArrivedWithoutAConcept()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = TestValues.NewGameId();
        var ratingWithoutAConcept = NoSignals() with
        {
            PsnEnriched = false,
            PsnRating = TestValues.NewStarRating(),
        };

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId, null, null, ratingWithoutAConcept, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(dataSource.ExecutedCommands).Parameters["@psn_enriched"].Value is false);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_StampsRawgAttemptedAt_WhenRawgActuallyAnswered()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var answeredGameId = TestValues.NewGameId();
        var answered = NoSignals() with { RawgAttempted = true };

        // Act
        await repository.SaveGameEnrichmentAsync(
            answeredGameId, null, null, answered, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(dataSource.ExecutedCommands).Parameters["@rawg_attempted"].Value is true);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_DoesNotStampRawgAttemptedAt_WhenRawgWasNeverAsked()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var neverAskedGameId = TestValues.NewGameId();
        var neverAsked = NoSignals() with { RawgAttempted = false };

        // Act
        await repository.SaveGameEnrichmentAsync(
            neverAskedGameId, null, null, neverAsked, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(dataSource.ExecutedCommands).Parameters["@rawg_attempted"].Value is false);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_KeepsAnEarlierRawgAttempt_RatherThanClearingItOnAPassThatNeverReachedRawg()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var neverAskedGameId = TestValues.NewGameId();

        // Act
        await repository.SaveGameEnrichmentAsync(
            neverAskedGameId, null, null, NoSignals(), TestContext.Current.CancellationToken);

        // Assert
        var sql = Assert.Single(dataSource.ExecutedCommands).ExecutedSql;
        Assert.Contains("ELSE game_enrichment.rawg_attempted_at", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("EXCLUDED.rawg_attempted_at", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CuratorGameEnrichmentColumnConstants.Developer)]
    [InlineData(CuratorGameEnrichmentColumnConstants.CriticalScore)]
    [InlineData(CuratorGameEnrichmentColumnConstants.ScoreSource)]
    public async Task SaveGameEnrichmentAsync_KeepsARawgSourcedColumn_RatherThanNullingItOnAPassThatNeverReachedRawg(
        string rawgSourcedColumn)
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var neverAskedGameId = TestValues.NewGameId();

        // Act
        await repository.SaveGameEnrichmentAsync(
            neverAskedGameId, null, null, NoSignals(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            $"COALESCE(EXCLUDED.{rawgSourcedColumn}, game_enrichment.{rawgSourcedColumn})",
            Assert.Single(dataSource.ExecutedCommands).ExecutedSql,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CuratorGameEnrichmentColumnConstants.GenreId)]
    [InlineData(CuratorGameEnrichmentColumnConstants.SubgenreId)]
    [InlineData(CuratorGameEnrichmentColumnConstants.ReleaseYear)]
    [InlineData(CuratorGameEnrichmentColumnConstants.Publisher)]
    [InlineData(CuratorGameEnrichmentColumnConstants.Esrb)]
    [InlineData(CuratorGameEnrichmentColumnConstants.Multiplayer)]
    [InlineData(CuratorGameEnrichmentColumnConstants.AaaTier)]
    public async Task SaveGameEnrichmentAsync_GuardsASharedColumnOnProviderSuccess_NotOnTheProviderMerelyBeingAsked(
        string sharedColumn)
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = TestValues.NewGameId();

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId, null, null, NoSignals(), TestContext.Current.CancellationToken);

        // Assert
        var sql = Assert.Single(dataSource.ExecutedCommands).ExecutedSql;
        Assert.Contains(
            $"WHEN @rawg_enriched OR @psn_enriched THEN EXCLUDED.{sharedColumn}",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"WHEN @rawg_attempted OR @psn_attempted THEN EXCLUDED.{sharedColumn}",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_StoresANullGenreAndSubgenre_WhenNeitherWasResolved()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = TestValues.NewGameId();
        var signals = new GameEnrichmentSignals(
            null, null, null, null, null, null, null, null, null, null, null, null, false, false);

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId,
            null,
            null,
            signals,
            TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(DBNull.Value, command.Parameters["@genre_id"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["@subgenre_id"].Value);
    }

    [Fact]
    public async Task ListPublisherTierRulesAsync_MapsRows()
    {
        // Arrange
        var table = new DataTable();
        table.Columns.Add("tier_id", typeof(Guid));
        table.Columns.Add("pattern", typeof(string));
        table.Columns.Add("tier", typeof(string));
        table.Columns.Add("match_kind", typeof(string));
        var tierId = Guid.NewGuid();
        var pattern = TestValues.NewPublisher();
        table.Rows.Add(tierId, pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var rules = await repository.ListPublisherTierRulesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            [new PublisherTierRule(tierId, pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind)],
            rules);
    }

    [Fact]
    public async Task GetPublisherTierRulesFingerprintAsync_ReturnsNull_WhenTheReclassificationPassHasNeverRun()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var fingerprint = await repository.GetPublisherTierRulesFingerprintAsync(
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(fingerprint);
    }

    [Fact]
    public async Task GetPublisherTierRulesFingerprintAsync_ReturnsTheStoredValue()
    {
        // Arrange
        var storedFingerprint = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(storedFingerprint));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var fingerprint = await repository.GetPublisherTierRulesFingerprintAsync(
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(storedFingerprint, fingerprint);
    }

    [Fact]
    public async Task SetPublisherTierRulesFingerprintAsync_UpsertsThePassStateRow()
    {
        // Arrange
        var fingerprint = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SetPublisherTierRulesFingerprintAsync(fingerprint, TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Contains("curation_rule_pass_state", command.CapturedCommandText, StringComparison.Ordinal);
        Assert.Equal(CurationPassNames.TierReclassification, command.Parameters["@pass_name"].Value);
        Assert.Equal(fingerprint, command.Parameters["@fingerprint"].Value);
    }

    [Fact]
    public async Task ReclassifyTierAsync_UpdatesOnlyTheRowsWhoseTierActuallyChanged_InOneStatement()
    {
        // Arrange
        var unchangedId = Guid.NewGuid();
        var changedId = Guid.NewGuid();
        var table = GameEnrichmentTierTable();
        var promotedPublisherPattern = TestValues.NewPublisherPattern();
        var unchangedPublisherPattern = TestValues.NewPublisherPattern();
        table.Rows.Add(
            changedId, promotedPublisherPattern.ToUpperInvariant(), DBNull.Value, PublisherTierRuleSet.IndieTier);
        table.Rows.Add(
            unchangedId, unchangedPublisherPattern.ToUpperInvariant(), DBNull.Value, PublisherTierRuleSet.AaTier);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);
        var promotedTierId = Guid.NewGuid();
        var unchangedTierId = Guid.NewGuid();
        var rules = new List<PublisherTierRule>
        {
            new(
                promotedTierId,
                promotedPublisherPattern,
                PublisherTierRuleSet.AaaTier,
                PublisherTierRuleSet.SubstringMatchKind),
            new(
                unchangedTierId,
                unchangedPublisherPattern,
                PublisherTierRuleSet.AaTier,
                PublisherTierRuleSet.SubstringMatchKind),
        };

        // Act
        var updated = await repository.ReclassifyTierAsync(rules, TestContext.Current.CancellationToken);

        // Assert
        var update = TierUpdate(dataSource);
        var gameIds = Assert.IsType<Guid[]>(update.Parameters["@game_ids"].Value);
        Assert.Equal([changedId], gameIds);
        Assert.Equal([PublisherTierRuleSet.AaaTier], Assert.IsType<string[]>(update.Parameters["@aaa_tiers"].Value));
        Assert.Equal(gameIds.Length, updated);
        Assert.Contains("unnest(@game_ids, @aaa_tiers)", update.ExecutedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReclassifyTierAsync_FallsBackToTheDeveloperAndThenToIndie()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var developer = TestValues.NewPublisher();
        var table = GameEnrichmentTierTable();
        table.Rows.Add(gameId, DBNull.Value, developer, PublisherTierRuleSet.IndieTier);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var updated = await repository.ReclassifyTierAsync([], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, updated);
        Assert.Single(dataSource.ExecutedCommands);
    }

    [Fact]
    public async Task ReclassifyTierAsync_ClearsATierItCanNoLongerJustify()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var table = GameEnrichmentTierTable();
        table.Rows.Add(gameId, string.Empty, string.Empty, PublisherTierRuleSet.IndieTier);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        var updated = await repository.ReclassifyTierAsync([], TestContext.Current.CancellationToken);

        // Assert
        var update = TierUpdate(dataSource);
        var gameIds = Assert.IsType<Guid[]>(update.Parameters["@game_ids"].Value);
        Assert.Equal(gameId, Assert.Single(gameIds));
        Assert.Null(Assert.Single(Assert.IsType<string[]>(update.Parameters["@aaa_tiers"].Value)));
        Assert.Equal(gameIds.Length, updated);
    }

    private static FakeDbCommand TierUpdate(FakeDbDataSource dataSource) =>
        Assert.Single(dataSource.ExecutedCommands, command => command.ExecutedSql.Contains("UPDATE game_enrichment", StringComparison.Ordinal));

    private static DataTable GameEnrichmentTierTable()
    {
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("publisher", typeof(string));
        table.Columns.Add("developer", typeof(string));
        table.Columns.Add("aaa_tier", typeof(string));
        return table;
    }

    private static PsnCatalogCacheEntry NewConceptWithoutACoverImage() =>
        NewConceptWithoutACoverImage(TestValues.NewTitleId(), TestValues.NewGenre());

    private static PsnCatalogCacheEntry NewConceptWithoutACoverImage(string titleId, params string[] genres) =>
        new(
            titleId,
            TestValues.NewConceptId(),
            genres,
            TestValues.NewStarRating(),
            TestValues.NewPublisher(),
            TestValues.NewReleaseDate(),
            CoverImageUrl: null);

    private static DataTable PsnCatalogCacheTable()
    {
        var table = new DataTable();
        table.Columns.Add("title_id", typeof(string));
        table.Columns.Add("concept_id", typeof(string));
        table.Columns.Add("genres", typeof(object));
        table.Columns.Add("star_rating", typeof(double));
        table.Columns.Add("publisher", typeof(string));
        table.Columns.Add("release_date", typeof(DateOnly));
        table.Columns.Add("cover_image_url", typeof(string));
        table.Columns.Add("content_rating", typeof(string));
        table.Columns.Add("rating_authority", typeof(string));
        table.Columns.Add("multiplayer", typeof(bool));
        table.Columns.Add("concept_fetched_at", typeof(DateTimeOffset));
        table.Columns.Add("concept_type", typeof(string));
        return table;
    }

    private static GameEnrichmentSignals NoSignals() => new(
        null, null, null, null, null, null, null, null, null, null, null, null, false, false);
}
