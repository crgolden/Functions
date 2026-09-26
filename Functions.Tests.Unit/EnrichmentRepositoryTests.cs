namespace Functions.Tests.Unit;

using System.Data;
using System.Text.Json;
using Functions.Curator;
using Functions.Curator.Catalog;
using Functions.Curator.Enrichment;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Store;
using Functions.Tests.Unit.TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentRepositoryTests
{
    [Fact]
    public async Task GetAllOpenCriticGamesAsync_MapsRows_KeepingNullColumnsNullRatherThanEmpty()
    {
        // Arrange
        var gameOneId = Generated.NewOpenCriticGameId();
        var gameOneName = Generated.NewGameTitle();
        var topCriticScore = Generated.NewCriticScore();
        var tier = Generated.NewOpenCriticTier();
        var percentRecommended = Generated.NewPercentRecommended();
        var gameTwoId = Generated.NewOpenCriticGameId();
        var gameTwoName = Generated.NewGameTitle();
        var table = FakeResultSet.WithColumns(
            typeof(int),
            typeof(string),
            typeof(double),
            typeof(string),
            typeof(double));
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
        var missingTitle = Generated.NewGameTitle();
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
        var table = FakeResultSet.WithColumns(
            typeof(string),
            typeof(int),
            typeof(string));
        var normalizedTitle = Generated.NewGameTitle();
        var titleAsTheCallerSpellsIt = $"{normalizedTitle.ToUpperInvariant()}™";
        var rawgGameId = Generated.NewRawgGameId();
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
        Assert.Equal(normalizedTitle, command.Parameters[CuratorSqlParameters.NormalizedTitle].Value);
    }

    [Fact]
    public async Task SaveRawgCacheAsync_NormalizesTitleAndStoresNullMatch()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var normalizedTitle = Generated.NewGameTitle();

        // Act
        await repository.SaveRawgCacheAsync(
            normalizedTitle.ToUpperInvariant(),
            null,
            null,
            TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(normalizedTitle, command.Parameters[CuratorSqlParameters.NormalizedTitle].Value);
        Assert.Equal(DBNull.Value, command.Parameters[CuratorSqlParameters.RawgGameId].Value);
        Assert.Equal(DBNull.Value, command.Parameters[CuratorSqlParameters.Raw].Value);
        Assert.Contains("INSERT INTO rawg_cache", command.CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPsnCatalogCacheAsync_ReturnsNull_WhenNoRow()
    {
        // Arrange
        var missingTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
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
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var conceptId = Generated.NewConceptId();
        var genres = new[] { Generated.NewGenre(), Generated.NewGenre() };
        var starRating = Generated.NewStarRating();
        var publisher = Generated.NewPublisher();
        var releaseDate = Generated.NewReleaseDate();
        var coverImageUrl = Generated.NewCoverImageAddress();
        var contentRating = Generated.NewContentRating();
        var ratingAuthority = Generated.NewRatingAuthority();
        var resolvedAt = Generated.NewUtcTimestamp();
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
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var coverImageUrl = Generated.NewCoverImageAddress();
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
            insertBranchSql => Assert.Contains("concept_fetched_at", insertBranchSql, StringComparison.Ordinal),
            updateBranchSql => Assert.Contains("concept_fetched_at = now()", updateBranchSql, StringComparison.Ordinal));
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
        Assert.Equal(DBNull.Value, command.Parameters[CuratorSqlParameters.CoverImageUrl].Value);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_SendsTheGenresArrayAndCoreFields()
    {
        // Arrange
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var genres = new[] { Generated.NewGenre() };
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SavePsnCatalogCacheAsync(
            NewConceptWithoutACoverImage(titleId, genres),
            TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(titleId, command.Parameters[CuratorSqlParameters.TitleId].Value);
        Assert.Equal(genres, command.Parameters[CuratorSqlParameters.Genres].Value);
        Assert.Contains("INSERT INTO psn_catalog_cache", command.CapturedCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_SendsTheConceptTypeOnBothUpsertBranches()
    {
        // Arrange
        var conceptType = Generated.NewToken();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        // Act
        await repository.SavePsnCatalogCacheAsync(
            NewConceptWithoutACoverImage() with { ConceptType = conceptType },
            TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(dataSource.ExecutedCommands);
        Assert.Equal(conceptType, command.Parameters[CuratorSqlParameters.ConceptType].Value);
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
        Assert.Equal(ContentKinds.MediaApp, classify.Parameters[CuratorSqlParameters.ContentKind].Value);
        Assert.Equal(entry.ConceptId, classify.Parameters[CuratorSqlParameters.ConceptId].Value);
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
            NewConceptWithoutACoverImage() with { ConceptType = Generated.NewToken() },
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
        var candidateId = Generated.NewGameId();
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(bool),
            typeof(bool),
            typeof(bool));
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
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(int));
        var shooterId = Guid.NewGuid();
        var rpgId = Guid.NewGuid();
        var shooterName = Generated.NewGenre();
        var rpgName = Generated.NewGenre();
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
        var gameId = Generated.NewGameId();
        var genreId = Guid.NewGuid();
        var subgenreId = Guid.NewGuid();
        var releaseYear = Generated.NewReleaseYear();
        var developer = Generated.NewPublisher();
        var publisher = Generated.NewPublisher();
        var esrb = Generated.NewToken();
        var criticalScore = Generated.NewCriticScore();
        var ocScore = Generated.NewCriticScore();
        var ocTier = Generated.NewOpenCriticTier();
        var ocPercentRecommended = Generated.NewPercentRecommended();
        var psnRating = Generated.NewStarRating();
        var psnRatingCount = Generated.NewPsnRatingCount();
        var scoreSource = Generated.NewToken();
        var aaaTier = Generated.NewToken();
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
        Assert.Equal(gameId, command.Parameters[CuratorSqlParameters.GameId].Value);
        Assert.Equal(genreId, command.Parameters[CuratorSqlParameters.GenreId].Value);
        Assert.Equal(subgenreId, command.Parameters[CuratorSqlParameters.SubgenreId].Value);
        Assert.Equal(psnRating, command.Parameters[CuratorSqlParameters.PsnRating].Value);
        Assert.Equal(psnRatingCount, command.Parameters[CuratorSqlParameters.PsnRatingCount].Value);
        Assert.Contains("psn_rating_count = CASE", command.ExecutedSql, StringComparison.Ordinal);
        Assert.True(command.Parameters[CuratorSqlParameters.RawgEnriched].Value is true);
        Assert.True(command.Parameters[CuratorSqlParameters.OpencriticEnriched].Value is true);
    }

    [Fact]
    public async Task GetStoreProductsNeedingPsnEnrichmentAsync_AsksForStoreProductsNotYetEnrichedByPsn_NeverAttemptedFirst()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var title = Generated.NewGameTitle();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var storeProductId = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix);
        var limit = Random.Shared.Next(1, 500);
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(string));
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
        Assert.Equal(limit, command.Parameters[CuratorSqlParameters.Limit].Value);
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
        Assert.Equal(CuratorAdvisoryLocks.EnrichmentRun, command.Parameters[CuratorSqlParameters.LockClass].Value);
        Assert.Equal(EnrichmentRepository.StoreProductPassLockKey, command.Parameters[CuratorSqlParameters.LockKey].Value);
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
        var name = Generated.NewGenre();
        var displayName = Generated.NewGenreDisplayName();
        var priority = Generated.NewRulePriority();
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(int));
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
        var gameId = Generated.NewGameId();
        var conceptWithoutARating = NoSignals() with { PsnEnriched = true, PsnRating = null };

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId, null, null, conceptWithoutARating, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(dataSource.ExecutedCommands).Parameters[CuratorSqlParameters.PsnEnriched].Value is true);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_LeavesPsnEnrichedFalse_WhenAStarRatingArrivedWithoutAConcept()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = Generated.NewGameId();
        var ratingWithoutAConcept = NoSignals() with
        {
            PsnEnriched = false,
            PsnRating = Generated.NewStarRating(),
        };

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId, null, null, ratingWithoutAConcept, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(dataSource.ExecutedCommands).Parameters[CuratorSqlParameters.PsnEnriched].Value is false);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_StampsRawgAttemptedAt_WhenRawgActuallyAnswered()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var answeredGameId = Generated.NewGameId();
        var answered = NoSignals() with { RawgAttempted = true };

        // Act
        await repository.SaveGameEnrichmentAsync(
            answeredGameId, null, null, answered, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(dataSource.ExecutedCommands).Parameters[CuratorSqlParameters.RawgAttempted].Value is true);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_DoesNotStampRawgAttemptedAt_WhenRawgWasNeverAsked()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var neverAskedGameId = Generated.NewGameId();
        var neverAsked = NoSignals() with { RawgAttempted = false };

        // Act
        await repository.SaveGameEnrichmentAsync(
            neverAskedGameId, null, null, neverAsked, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(dataSource.ExecutedCommands).Parameters[CuratorSqlParameters.RawgAttempted].Value is false);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_KeepsAnEarlierRawgAttempt_RatherThanClearingItOnAPassThatNeverReachedRawg()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var neverAskedGameId = Generated.NewGameId();

        // Act
        await repository.SaveGameEnrichmentAsync(
            neverAskedGameId, null, null, NoSignals(), TestContext.Current.CancellationToken);

        // Assert
        var sql = Assert.Single(dataSource.ExecutedCommands).ExecutedSql;
        Assert.Contains("ELSE game_enrichment.rawg_attempted_at", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("EXCLUDED.rawg_attempted_at", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GameEnrichmentColumns.Developer)]
    [InlineData(GameEnrichmentColumns.CriticalScore)]
    [InlineData(GameEnrichmentColumns.ScoreSource)]
    public async Task SaveGameEnrichmentAsync_KeepsARawgSourcedColumn_RatherThanNullingItOnAPassThatNeverReachedRawg(
        string rawgSourcedColumn)
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var neverAskedGameId = Generated.NewGameId();

        // Act
        await repository.SaveGameEnrichmentAsync(
            neverAskedGameId, null, null, NoSignals(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            $"COALESCE(EXCLUDED.{rawgSourcedColumn}, {GameEnrichmentColumns.Table}.{rawgSourcedColumn})",
            Assert.Single(dataSource.ExecutedCommands).ExecutedSql,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GameEnrichmentColumns.GenreId)]
    [InlineData(GameEnrichmentColumns.SubgenreId)]
    [InlineData(GameEnrichmentColumns.ReleaseYear)]
    [InlineData(GameEnrichmentColumns.Publisher)]
    [InlineData(GameEnrichmentColumns.Esrb)]
    [InlineData(GameEnrichmentColumns.Multiplayer)]
    [InlineData(GameEnrichmentColumns.AaaTier)]
    public async Task SaveGameEnrichmentAsync_GuardsASharedColumnOnProviderSuccess_NotOnTheProviderMerelyBeingAsked(
        string sharedColumn)
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = Generated.NewGameId();

        // Act
        await repository.SaveGameEnrichmentAsync(
            gameId, null, null, NoSignals(), TestContext.Current.CancellationToken);

        // Assert
        var sql = Assert.Single(dataSource.ExecutedCommands).ExecutedSql;
        Assert.Contains(
            $"WHEN {CuratorSqlParameters.RawgEnriched} OR {CuratorSqlParameters.PsnEnriched} THEN EXCLUDED.{sharedColumn}",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"WHEN {CuratorSqlParameters.RawgAttempted} OR {CuratorSqlParameters.PsnAttempted} THEN EXCLUDED.{sharedColumn}",
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
        var gameId = Generated.NewGameId();
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
        Assert.Equal(DBNull.Value, command.Parameters[CuratorSqlParameters.GenreId].Value);
        Assert.Equal(DBNull.Value, command.Parameters[CuratorSqlParameters.SubgenreId].Value);
    }

    [Fact]
    public async Task ListPublisherTierRulesAsync_MapsRows()
    {
        // Arrange
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(string));
        var tierId = Guid.NewGuid();
        var pattern = Generated.NewPublisher();
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
        Assert.Equal(CurationPassNames.TierReclassification, command.Parameters[CuratorSqlParameters.PassName].Value);
        Assert.Equal(fingerprint, command.Parameters[CuratorSqlParameters.Fingerprint].Value);
    }

    [Fact]
    public async Task ReclassifyTierAsync_UpdatesOnlyTheRowsWhoseTierActuallyChanged_InOneStatement()
    {
        // Arrange
        var unchangedId = Guid.NewGuid();
        var changedId = Guid.NewGuid();
        var table = GameEnrichmentTierTable();
        var promotedPublisherPattern = Generated.NewPublisherPattern();
        var unchangedPublisherPattern = Generated.NewPublisherPattern();
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
        var gameIds = Assert.IsType<Guid[]>(update.Parameters[CuratorSqlParameters.GameIds].Value);
        Assert.Equal([changedId], gameIds);
        Assert.Equal([PublisherTierRuleSet.AaaTier], Assert.IsType<string[]>(update.Parameters[CuratorSqlParameters.AaaTiers].Value));
        Assert.Equal(gameIds.Length, updated);
        Assert.Contains("unnest(@game_ids, @aaa_tiers)", update.ExecutedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReclassifyTierAsync_FallsBackToTheDeveloperAndThenToIndie()
    {
        // Arrange
        var gameId = Guid.NewGuid();
        var developer = Generated.NewPublisher();
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
        var gameIds = Assert.IsType<Guid[]>(update.Parameters[CuratorSqlParameters.GameIds].Value);
        Assert.Equal(gameId, Assert.Single(gameIds));
        Assert.Null(Assert.Single(Assert.IsType<string[]>(update.Parameters[CuratorSqlParameters.AaaTiers].Value)));
        Assert.Equal(gameIds.Length, updated);
    }

    private static FakeDbCommand TierUpdate(FakeDbDataSource dataSource) =>
        Assert.Single(dataSource.ExecutedCommands, command => command.ExecutedSql.Contains("UPDATE game_enrichment", StringComparison.Ordinal));

    private static DataTable GameEnrichmentTierTable()
    {
        var table = FakeResultSet.WithColumns(
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(string));
        return table;
    }

    private static PsnCatalogCacheEntry NewConceptWithoutACoverImage() =>
        NewConceptWithoutACoverImage(Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), Generated.NewGenre());

    private static PsnCatalogCacheEntry NewConceptWithoutACoverImage(string titleId, params string[] genres) =>
        new(
            titleId,
            Generated.NewConceptId(),
            genres,
            Generated.NewStarRating(),
            Generated.NewPublisher(),
            Generated.NewReleaseDate(),
            CoverImageUrl: null);

    private static DataTable PsnCatalogCacheTable()
    {
        var table = FakeResultSet.WithColumns(
            typeof(string),
            typeof(string),
            typeof(object),
            typeof(double),
            typeof(string),
            typeof(DateOnly),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(bool),
            typeof(DateTimeOffset),
            typeof(string));
        return table;
    }

    private static GameEnrichmentSignals NoSignals() => new(
        null, null, null, null, null, null, null, null, null, null, null, null, false, false);
}
