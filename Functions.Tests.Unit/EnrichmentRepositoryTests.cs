namespace Functions.Tests.Unit;

using System.Data;
using Curator.Enrichment;
using Curator.OpenCritic;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentRepositoryTests
{
    [Fact]
    public async Task GetAllOpenCriticGamesAsync_MapsRows_WithNullTierDefaultingToEmptyString()
    {
        var gameOneId = Random.Shared.Next(1, 100_000);
        var gameOneName = $"Game-{Guid.NewGuid():N}";
        var topCriticScore = Math.Round(Random.Shared.NextDouble() * 100, 2);
        var tier = $"Tier-{Guid.NewGuid():N}";
        var percentRecommended = Math.Round(Random.Shared.NextDouble() * 100, 2);
        var gameTwoId = Random.Shared.Next(1, 100_000);
        var gameTwoName = $"Game-{Guid.NewGuid():N}";
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

        var games = await repository.GetAllOpenCriticGamesAsync(TestContext.Current.CancellationToken);

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
        var missingTitle = $"Game-{Guid.NewGuid():N}";
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var repository = new EnrichmentRepository(dataSource);

        var entry = await repository.GetRawgCacheAsync(missingTitle, TestContext.Current.CancellationToken);

        Assert.Null(entry);
    }

    [Fact]
    public async Task GetRawgCacheAsync_NormalizesTheTitleBeforeLookup()
    {
        var table = new DataTable();
        table.Columns.Add("normalized_title", typeof(string));
        table.Columns.Add("rawg_game_id", typeof(int));
        table.Columns.Add("raw", typeof(string));
        table.Rows.Add("god of war", 123, """{"id":123}""");
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        var entry = await repository.GetRawgCacheAsync("God of War™", TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal(123, entry.RawgGameId);
        Assert.Equal("""{"id":123}""", entry.Raw);
        var command = dataSource.ExecutedCommands[0];
        Assert.Equal("god of war", command.Parameters["@normalized_title"].Value);
    }

    [Fact]
    public async Task SaveRawgCacheAsync_NormalizesTitleAndStoresNullMatch()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        await repository.SaveRawgCacheAsync(
            "Unknown Game",
            null,
            null,
            TestContext.Current.CancellationToken);

        var command = dataSource.ExecutedCommands[0];
        Assert.Equal("unknown game", command.Parameters["@normalized_title"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["@rawg_game_id"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["@raw"].Value);
        Assert.Contains("INSERT INTO rawg_cache", command.CapturedCommandText);
    }

    [Fact]
    public async Task GetPsnCatalogCacheAsync_ReturnsNull_WhenNoRow()
    {
        var missingTitleId = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(new DataTable()));
        var repository = new EnrichmentRepository(dataSource);

        var entry = await repository.GetPsnCatalogCacheAsync(missingTitleId, TestContext.Current.CancellationToken);

        Assert.Null(entry);
    }

    [Fact]
    public async Task GetPsnCatalogCacheAsync_MapsACompletedLookupRow()
    {
        var titleId = Guid.NewGuid().ToString();
        var conceptId = Guid.NewGuid().ToString();
        var genres = new[] { $"Genre-{Guid.NewGuid():N}", $"Genre-{Guid.NewGuid():N}" };
        var starRating = Math.Round(Random.Shared.NextDouble() * 5, 2);
        var publisher = $"Publisher-{Guid.NewGuid():N}";
        var releaseDate = new DateOnly(2000, 1, 1).AddDays(Random.Shared.Next(0, 9_000));
        var coverImageUrl = $"https://example.invalid/{Guid.NewGuid():N}.png";
        var contentRating = $"Rating-{Guid.NewGuid():N}";
        var ratingAuthority = $"Authority-{Guid.NewGuid():N}";
        var resolvedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 365));
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

        var entry = await repository.GetPsnCatalogCacheAsync(titleId, TestContext.Current.CancellationToken);

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
        var titleId = Guid.NewGuid().ToString();
        var coverImageUrl = $"https://example.invalid/{Guid.NewGuid():N}.png";
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

        var entry = await repository.GetPsnCatalogCacheAsync(titleId, TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Null(entry.ConceptFetchedAt);
        Assert.Null(entry.ConceptId);
        Assert.Equal(coverImageUrl, entry.CoverImageUrl);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_StampsConceptFetchedAtOnBothUpsertBranches()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        await repository.SavePsnCatalogCacheAsync(NewPsnCatalogCacheEntry(), TestContext.Current.CancellationToken);

        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        var parts = sql.Split("DO UPDATE SET");
        Assert.Equal(2, parts.Length);
        Assert.Contains("concept_fetched_at", parts[0]);
        Assert.Contains("concept_fetched_at = now()", parts[1]);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_KeepsASeededCoverImage_WhenTheIncomingConceptCarriesNone()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        await repository.SavePsnCatalogCacheAsync(NewPsnCatalogCacheEntry(), TestContext.Current.CancellationToken);

        var sql = dataSource.ExecutedCommands[0].ExecutedSql;
        Assert.Contains(
            "cover_image_url = COALESCE(EXCLUDED.cover_image_url, psn_catalog_cache.cover_image_url)",
            sql);
        var command = dataSource.ExecutedCommands[0];
        Assert.Equal(DBNull.Value, command.Parameters["@cover_image_url"].Value);
    }

    [Fact]
    public async Task SavePsnCatalogCacheAsync_SendsTheGenresArrayAndCoreFields()
    {
        var titleId = Guid.NewGuid().ToString();
        var genres = new[] { $"Genre-{Guid.NewGuid():N}" };
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        await repository.SavePsnCatalogCacheAsync(
            NewPsnCatalogCacheEntry(titleId, genres),
            TestContext.Current.CancellationToken);

        var command = dataSource.ExecutedCommands[0];
        Assert.Equal(titleId, command.Parameters["@title_id"].Value);
        Assert.Equal(genres, command.Parameters["@genres"].Value);
        Assert.Contains("INSERT INTO psn_catalog_cache", command.CapturedCommandText);
    }

    [Fact]
    public async Task GetUnenrichedGameIdsAsync_ReturnsEmpty_WithoutOpeningAConnection_WhenNoCandidateIds()
    {
        var dataSource = new FakeDbDataSource();
        var repository = new EnrichmentRepository(dataSource);

        var unenriched = await repository.GetUnenrichedGameIdsAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(unenriched);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    [Fact]
    public async Task GetUnenrichedGameIdsAsync_ReturnsTheSubsetWithNoEnrichmentRowYet()
    {
        var unenrichedId = Guid.NewGuid();
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Rows.Add(unenrichedId);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);
        var candidateId = Guid.NewGuid().ToString();

        var unenriched = await repository.GetUnenrichedGameIdsAsync(
            [candidateId, unenrichedId.ToString()],
            TestContext.Current.CancellationToken);

        Assert.Equal([unenrichedId.ToString()], unenriched);
        var command = dataSource.ExecutedCommands[0];
        Assert.Contains("unnest(@game_ids::uuid[])", command.CapturedCommandText);
    }

    [Fact]
    public async Task GetActiveGenresAsync_MapsRows()
    {
        var table = new DataTable();
        table.Columns.Add("genre_id", typeof(Guid));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("priority", typeof(int));
        var shooterId = Guid.NewGuid();
        var rpgId = Guid.NewGuid();
        var shooterName = $"Genre-{Guid.NewGuid():N}";
        var rpgName = $"Genre-{Guid.NewGuid():N}";
        var shooterPriority = Random.Shared.Next(0, 100);
        var rpgPriority = Random.Shared.Next(0, 100);
        table.Rows.Add(shooterId, shooterName, shooterPriority);
        table.Rows.Add(rpgId, rpgName, rpgPriority);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        var genres = await repository.GetActiveGenresAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new ActiveGenre(shooterId.ToString(), shooterName, shooterPriority),
                new ActiveGenre(rpgId.ToString(), rpgName, rpgPriority),
            ],
            genres);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_ExecutesUpsertWithEverySignal()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = Guid.NewGuid().ToString();
        var genreId = Guid.NewGuid().ToString();
        var subgenreId = Guid.NewGuid().ToString();
        var releaseYear = Random.Shared.Next(1990, 2030);
        var developer = $"Developer-{Guid.NewGuid():N}";
        var publisher = $"Publisher-{Guid.NewGuid():N}";
        var esrb = $"Esrb-{Guid.NewGuid():N}";
        var criticalScore = Math.Round(Random.Shared.NextDouble() * 100, 2);
        var ocScore = Math.Round(Random.Shared.NextDouble() * 100, 2);
        var ocTier = $"Tier-{Guid.NewGuid():N}";
        var ocPercentRecommended = Math.Round(Random.Shared.NextDouble() * 100, 2);
        var psnRating = Math.Round(Random.Shared.NextDouble() * 5, 2);
        var scoreSource = $"Source-{Guid.NewGuid():N}";
        var aaaTier = $"AaaTier-{Guid.NewGuid():N}";
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
            true);

        await repository.SaveGameEnrichmentAsync(
            gameId,
            genreId,
            subgenreId,
            signals,
            TestContext.Current.CancellationToken);

        var command = dataSource.ExecutedCommands[0];
        Assert.Contains("INSERT INTO game_enrichment", command.CapturedCommandText);
        Assert.Equal(Guid.Parse(gameId), command.Parameters["@game_id"].Value);
        Assert.Equal(Guid.Parse(genreId), command.Parameters["@genre_id"].Value);
        Assert.Equal(Guid.Parse(subgenreId), command.Parameters["@subgenre_id"].Value);
        Assert.Equal(psnRating, command.Parameters["@psn_rating"].Value);
        Assert.True(command.Parameters["@rawg_enriched"].Value is true);
        Assert.True(command.Parameters["@opencritic_enriched"].Value is true);
    }

    [Fact]
    public async Task SaveGameEnrichmentAsync_StoresANullGenreAndSubgenre_WhenNeitherWasResolved()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var gameId = Guid.NewGuid().ToString();
        var signals = new GameEnrichmentSignals(
            null, null, null, null, null, null, null, null, null, null, null, null, false, false);

        await repository.SaveGameEnrichmentAsync(
            gameId,
            null,
            null,
            signals,
            TestContext.Current.CancellationToken);

        var command = dataSource.ExecutedCommands[0];
        Assert.Equal(DBNull.Value, command.Parameters["@genre_id"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["@subgenre_id"].Value);
    }

    [Fact]
    public async Task ListPublisherTierRulesAsync_MapsRows()
    {
        var table = new DataTable();
        table.Columns.Add("tier_id", typeof(Guid));
        table.Columns.Add("pattern", typeof(string));
        table.Columns.Add("tier", typeof(string));
        table.Columns.Add("match_kind", typeof(string));
        var tierId = Guid.NewGuid();
        var pattern = $"Publisher-{Guid.NewGuid():N}";
        table.Rows.Add(tierId, pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        var rules = await repository.ListPublisherTierRulesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [new PublisherTierRule(tierId, pattern, PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind)],
            rules);
    }

    [Fact]
    public async Task GetPublisherTierRulesFingerprintAsync_ReturnsNull_WhenTheReclassificationPassHasNeverRun()
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(null));
        var repository = new EnrichmentRepository(dataSource);

        var fingerprint = await repository.GetPublisherTierRulesFingerprintAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(fingerprint);
    }

    [Fact]
    public async Task GetPublisherTierRulesFingerprintAsync_ReturnsTheStoredValue()
    {
        var storedFingerprint = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(storedFingerprint));
        var repository = new EnrichmentRepository(dataSource);

        var fingerprint = await repository.GetPublisherTierRulesFingerprintAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(storedFingerprint, fingerprint);
    }

    [Fact]
    public async Task SetPublisherTierRulesFingerprintAsync_UpsertsThePassStateRow()
    {
        var fingerprint = Guid.NewGuid().ToString();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        await repository.SetPublisherTierRulesFingerprintAsync(fingerprint, TestContext.Current.CancellationToken);

        var command = dataSource.ExecutedCommands[0];
        Assert.Contains("curation_rule_pass_state", command.CapturedCommandText);
        Assert.Equal(CurationPassNames.TierReclassification, command.Parameters["@pass_name"].Value);
        Assert.Equal(fingerprint, command.Parameters["@fingerprint"].Value);
    }

    [Fact]
    public async Task ReclassifyTierAsync_UpdatesOnlyTheRowsWhoseTierActuallyChanged()
    {
        var unchangedId = Guid.NewGuid();
        var changedId = Guid.NewGuid();
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("publisher", typeof(string));
        table.Columns.Add("developer", typeof(string));
        table.Columns.Add("aaa_tier", typeof(string));
        table.Rows.Add(changedId, "Ubisoft", DBNull.Value, PublisherTierRuleSet.IndieTier);
        table.Rows.Add(unchangedId, "Team17", DBNull.Value, PublisherTierRuleSet.AaTier);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);
        var ubisoftTierId = Guid.NewGuid();
        var team17TierId = Guid.NewGuid();
        var rules = new List<PublisherTierRule>
        {
            new(ubisoftTierId, "ubisoft", PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.SubstringMatchKind),
            new(team17TierId, "team17", PublisherTierRuleSet.AaTier, PublisherTierRuleSet.SubstringMatchKind),
        };

        var updated = await repository.ReclassifyTierAsync(rules, TestContext.Current.CancellationToken);

        Assert.Equal(1, updated);
        var updateCommand = dataSource.ExecutedCommands[1];
        Assert.Contains("UPDATE game_enrichment", updateCommand.CapturedCommandText);
        Assert.Equal(PublisherTierRuleSet.AaaTier, updateCommand.Parameters["@aaa_tier"].Value);
        Assert.Equal(changedId, updateCommand.Parameters["@game_id"].Value);
    }

    [Fact]
    public async Task ReclassifyTierAsync_FallsBackToTheDeveloperAndThenToIndie()
    {
        var gameId = Guid.NewGuid();
        var developer = $"Studio-{Guid.NewGuid():N}";
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("publisher", typeof(string));
        table.Columns.Add("developer", typeof(string));
        table.Columns.Add("aaa_tier", typeof(string));
        table.Rows.Add(gameId, DBNull.Value, developer, PublisherTierRuleSet.IndieTier);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        var repository = new EnrichmentRepository(dataSource);

        var updated = await repository.ReclassifyTierAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(0, updated);
        Assert.Single(dataSource.ExecutedCommands);
    }

    [Fact]
    public async Task ReclassifyTierAsync_ClearsATierItCanNoLongerJustify()
    {
        var gameId = Guid.NewGuid();
        var table = new DataTable();
        table.Columns.Add("game_id", typeof(Guid));
        table.Columns.Add("publisher", typeof(string));
        table.Columns.Add("developer", typeof(string));
        table.Columns.Add("aaa_tier", typeof(string));
        table.Rows.Add(gameId, string.Empty, string.Empty, PublisherTierRuleSet.IndieTier);
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithReader(table));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        var repository = new EnrichmentRepository(dataSource);

        var updated = await repository.ReclassifyTierAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(1, updated);
        var updateCommand = dataSource.ExecutedCommands[1];
        Assert.Equal(DBNull.Value, updateCommand.Parameters["@aaa_tier"].Value);
    }

    private static PsnCatalogCacheEntry NewPsnCatalogCacheEntry(
        string? titleId = null,
        IReadOnlyList<string>? genres = null,
        string? coverImageUrl = null) =>
        new(
            titleId ?? Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            genres ?? [$"Genre-{Guid.NewGuid():N}"],
            Math.Round(Random.Shared.NextDouble() * 5, 2),
            $"Publisher-{Guid.NewGuid():N}",
            new DateOnly(2000, 1, 1).AddDays(Random.Shared.Next(0, 9_000)),
            coverImageUrl);

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
        return table;
    }
}
