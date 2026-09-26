namespace Functions.Curator.Enrichment;

using System.Data.Common;
using Functions.Curator.Catalog;
using Functions.Curator.OpenCritic;
using Functions.Curator.Rawg;
using Functions.Curator.Store;
using Functions.Extensions;

public sealed class EnrichmentRepository
{
    internal const string CatalogEnrichmentPassLockKey = "catalog_enrichment_pass";
    internal const string StoreProductPassLockKey = "store_product_enrichment";

    private const string SaveGameEnrichmentSql = $"""
        INSERT INTO {GameEnrichmentColumns.Table} (
            game_id, {GameEnrichmentColumns.GenreId}, {GameEnrichmentColumns.SubgenreId}, {GameEnrichmentColumns.ReleaseYear}, {GameEnrichmentColumns.Developer}, {GameEnrichmentColumns.Publisher}, {GameEnrichmentColumns.Esrb}, {GameEnrichmentColumns.Multiplayer},
            {GameEnrichmentColumns.CriticalScore}, oc_score, oc_tier, oc_percent_recommended, psn_rating, psn_rating_count,
            {GameEnrichmentColumns.ScoreSource}, {GameEnrichmentColumns.AaaTier}, rawg_enriched, opencritic_enriched, psn_enriched, rawg_attempted_at,
            opencritic_attempted_at, psn_attempted_at
        )
        VALUES (@game_id, @genre_id, @subgenre_id, @release_year, @developer, @publisher, @esrb,
                @multiplayer, @critical_score, @oc_score, @oc_tier, @oc_percent_recommended,
                @psn_rating, @psn_rating_count, @score_source, @aaa_tier, @rawg_enriched,
                @opencritic_enriched, @psn_enriched, CASE WHEN @rawg_attempted THEN now() END,
                CASE WHEN @opencritic_attempted THEN now() END,
                CASE WHEN @psn_attempted THEN now() END)
        ON CONFLICT (game_id) DO UPDATE SET
            {GameEnrichmentColumns.GenreId} = CASE
                WHEN @rawg_enriched OR @psn_enriched THEN EXCLUDED.{GameEnrichmentColumns.GenreId}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.GenreId}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.GenreId})
            END,
            {GameEnrichmentColumns.SubgenreId} = CASE
                WHEN @rawg_enriched OR @psn_enriched THEN EXCLUDED.{GameEnrichmentColumns.SubgenreId}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.SubgenreId}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.SubgenreId})
            END,
            {GameEnrichmentColumns.ReleaseYear} = CASE
                WHEN @rawg_enriched OR @psn_enriched THEN EXCLUDED.{GameEnrichmentColumns.ReleaseYear}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.ReleaseYear}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.ReleaseYear})
            END,
            {GameEnrichmentColumns.Developer} = CASE
                WHEN @rawg_enriched THEN EXCLUDED.{GameEnrichmentColumns.Developer}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.Developer}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.Developer})
            END,
            {GameEnrichmentColumns.Publisher} = CASE
                WHEN @rawg_enriched OR @psn_enriched THEN EXCLUDED.{GameEnrichmentColumns.Publisher}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.Publisher}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.Publisher})
            END,
            {GameEnrichmentColumns.Esrb} = CASE
                WHEN @rawg_enriched OR @psn_enriched THEN EXCLUDED.{GameEnrichmentColumns.Esrb}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.Esrb}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.Esrb})
            END,
            {GameEnrichmentColumns.Multiplayer} = CASE
                WHEN @rawg_enriched OR @psn_enriched THEN EXCLUDED.{GameEnrichmentColumns.Multiplayer}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.Multiplayer}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.Multiplayer})
            END,
            {GameEnrichmentColumns.CriticalScore} = CASE
                WHEN @rawg_enriched THEN EXCLUDED.{GameEnrichmentColumns.CriticalScore}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.CriticalScore}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.CriticalScore})
            END,
            oc_score = CASE
                WHEN @opencritic_enriched THEN EXCLUDED.oc_score
                ELSE COALESCE(EXCLUDED.oc_score, {GameEnrichmentColumns.Table}.oc_score)
            END,
            oc_tier = CASE
                WHEN @opencritic_enriched THEN EXCLUDED.oc_tier
                ELSE COALESCE(EXCLUDED.oc_tier, {GameEnrichmentColumns.Table}.oc_tier)
            END,
            oc_percent_recommended = CASE
                WHEN @opencritic_enriched THEN EXCLUDED.oc_percent_recommended
                ELSE COALESCE(EXCLUDED.oc_percent_recommended, {GameEnrichmentColumns.Table}.oc_percent_recommended)
            END,
            psn_rating = CASE
                WHEN @psn_enriched THEN EXCLUDED.psn_rating
                ELSE COALESCE(EXCLUDED.psn_rating, {GameEnrichmentColumns.Table}.psn_rating)
            END,
            psn_rating_count = CASE
                WHEN @psn_enriched THEN EXCLUDED.psn_rating_count
                ELSE COALESCE(EXCLUDED.psn_rating_count, {GameEnrichmentColumns.Table}.psn_rating_count)
            END,
            {GameEnrichmentColumns.ScoreSource} = CASE
                WHEN @rawg_enriched THEN EXCLUDED.{GameEnrichmentColumns.ScoreSource}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.ScoreSource}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.ScoreSource})
            END,
            {GameEnrichmentColumns.AaaTier} = CASE
                WHEN @rawg_enriched OR @psn_enriched THEN EXCLUDED.{GameEnrichmentColumns.AaaTier}
                ELSE COALESCE(EXCLUDED.{GameEnrichmentColumns.AaaTier}, {GameEnrichmentColumns.Table}.{GameEnrichmentColumns.AaaTier})
            END,
            rawg_enriched = {GameEnrichmentColumns.Table}.rawg_enriched OR EXCLUDED.rawg_enriched,
            opencritic_enriched = {GameEnrichmentColumns.Table}.opencritic_enriched OR EXCLUDED.opencritic_enriched,
            psn_enriched = {GameEnrichmentColumns.Table}.psn_enriched OR EXCLUDED.psn_enriched,
            rawg_attempted_at = CASE
                WHEN @rawg_attempted THEN now()
                ELSE {GameEnrichmentColumns.Table}.rawg_attempted_at
            END,
            opencritic_attempted_at = CASE
                WHEN @opencritic_attempted THEN now()
                ELSE {GameEnrichmentColumns.Table}.opencritic_attempted_at
            END,
            psn_attempted_at = CASE
                WHEN @psn_attempted THEN now()
                ELSE {GameEnrichmentColumns.Table}.psn_attempted_at
            END,
            enriched_at = now()
        """;

    private readonly DbDataSource _dataSource;

    public EnrichmentRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public Task<AdvisoryLockHandle> TryLockCatalogEnrichmentPassAsync(CancellationToken cancellationToken = default) =>
        AdvisoryLockHandle.TryAcquireAsync(
            _dataSource, CuratorAdvisoryLocks.EnrichmentRun, CatalogEnrichmentPassLockKey, cancellationToken);

    public Task<AdvisoryLockHandle> TryLockStoreProductPassAsync(CancellationToken cancellationToken = default) =>
        AdvisoryLockHandle.TryAcquireAsync(
            _dataSource, CuratorAdvisoryLocks.EnrichmentRun, StoreProductPassLockKey, cancellationToken);

    public async Task<List<StoreProductCandidate>> GetStoreProductsNeedingPsnEnrichmentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT g.game_id, g.canonical_title, c.title_id, c.store_product_id
            FROM games g
            JOIN psn_catalog_cache c ON c.game_id = g.game_id
            LEFT JOIN game_enrichment ge ON ge.game_id = g.game_id
            WHERE c.store_product_id IS NOT NULL
              AND COALESCE(ge.psn_enriched, false) = false
            ORDER BY ge.psn_attempted_at NULLS FIRST, g.game_id
            LIMIT @limit
            """;
        cmd.AddParam(CuratorSqlParameters.Limit, limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var candidates = new List<StoreProductCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new StoreProductCandidate(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return candidates;
    }

    public async Task<List<StoreGenre>> GetActiveGenresWithLabelsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT genre_id, name, display_name, priority FROM genres WHERE active = true";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var genres = new List<StoreGenre>();
        while (await reader.ReadAsync(cancellationToken))
        {
            genres.Add(new StoreGenre(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return genres;
    }

    public async Task<List<OpenCriticGame>> GetAllOpenCriticGamesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT oc_game_id, name, top_critic_score, tier, percent_recommended FROM opencritic_cache";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var games = new List<OpenCriticGame>();
        while (await reader.ReadAsync(cancellationToken))
        {
            games.Add(new OpenCriticGame(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }

        return games;
    }

    public async Task<RawgCacheEntry?> GetRawgCacheAsync(string title, CancellationToken cancellationToken = default)
    {
        var normalizedTitle = RawgMatcher.Normalize(title);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT normalized_title, rawg_game_id, raw FROM rawg_cache WHERE normalized_title = @normalized_title";
        cmd.AddParam(CuratorSqlParameters.NormalizedTitle, normalizedTitle);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RawgCacheEntry(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task SaveRawgCacheAsync(
        string title,
        int? rawgGameId,
        string? raw,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = RawgMatcher.Normalize(title);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rawg_cache (normalized_title, rawg_game_id, raw)
            VALUES (@normalized_title, @rawg_game_id, @raw::jsonb)
            ON CONFLICT (normalized_title) DO UPDATE SET
                rawg_game_id = EXCLUDED.rawg_game_id,
                raw = EXCLUDED.raw,
                fetched_at = now()
            """;
        cmd.AddParam(CuratorSqlParameters.NormalizedTitle, normalizedTitle);
        cmd.AddParam(CuratorSqlParameters.RawgGameId, rawgGameId);
        cmd.AddParam(CuratorSqlParameters.Raw, raw);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PsnCatalogCacheEntry?> GetPsnCatalogCacheAsync(
        string titleId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT title_id, concept_id, genres, star_rating, publisher, release_date, cover_image_url,
                   content_rating, rating_authority, multiplayer, concept_fetched_at, concept_type
            FROM psn_catalog_cache WHERE title_id = @title_id
            """;
        cmd.AddParam(CuratorSqlParameters.TitleId, titleId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PsnCatalogCacheEntry(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? [] : reader.GetValue(2) as string[] ?? [],
            reader.IsDBNull(3) ? null : reader.GetDouble(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetBoolean(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    public async Task SavePsnCatalogCacheAsync(
        PsnCatalogCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO psn_catalog_cache (title_id, concept_id, genres, star_rating, publisher,
                                            release_date, cover_image_url, content_rating,
                                            rating_authority, multiplayer, concept_fetched_at, concept_type)
            VALUES (@title_id, @concept_id, @genres, @star_rating, @publisher, @release_date::date,
                    @cover_image_url, @content_rating, @rating_authority, @multiplayer, now(), @concept_type)
            ON CONFLICT (title_id) DO UPDATE SET
                concept_id = EXCLUDED.concept_id,
                genres = EXCLUDED.genres,
                star_rating = EXCLUDED.star_rating,
                publisher = EXCLUDED.publisher,
                release_date = EXCLUDED.release_date,
                cover_image_url = COALESCE(EXCLUDED.cover_image_url, psn_catalog_cache.cover_image_url),
                content_rating = EXCLUDED.content_rating,
                rating_authority = EXCLUDED.rating_authority,
                multiplayer = EXCLUDED.multiplayer,
                concept_fetched_at = now(),
                concept_type = EXCLUDED.concept_type,
                fetched_at = now()
            """;
        cmd.AddParam(CuratorSqlParameters.TitleId, entry.TitleId);
        cmd.AddParam(CuratorSqlParameters.ConceptId, entry.ConceptId);
        cmd.AddParam(CuratorSqlParameters.Genres, entry.Genres.ToArray());
        cmd.AddParam(CuratorSqlParameters.StarRating, entry.StarRating);
        cmd.AddParam(CuratorSqlParameters.Publisher, entry.Publisher);
        cmd.AddParam(CuratorSqlParameters.ReleaseDate, entry.ReleaseDate);
        cmd.AddParam(CuratorSqlParameters.CoverImageUrl, entry.CoverImageUrl);
        cmd.AddParam(CuratorSqlParameters.ContentRating, entry.ContentRating);
        cmd.AddParam(CuratorSqlParameters.RatingAuthority, entry.RatingAuthority);
        cmd.AddParam(CuratorSqlParameters.Multiplayer, entry.Multiplayer);
        cmd.AddParam(CuratorSqlParameters.ConceptType, entry.ConceptType);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (entry.ConceptId is not null
            && string.Equals(entry.ConceptType, ContentKinds.ApplicationConceptType, StringComparison.Ordinal))
        {
            await using var classify = connection.CreateCommand();
            classify.CommandText = """
                UPDATE games SET content_kind = @content_kind, updated_at = now()
                WHERE game_id IN (SELECT game_id FROM game_concepts WHERE concept_id = @concept_id)
                  AND content_kind IS DISTINCT FROM @content_kind
                """;
            classify.AddParam(CuratorSqlParameters.ContentKind, ContentKinds.MediaApp);
            classify.AddParam(CuratorSqlParameters.ConceptId, entry.ConceptId);
            await classify.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<List<EnrichmentNeed>> GetEnrichmentNeedsAsync(
        IReadOnlyCollection<Guid> gameIds,
        CancellationToken cancellationToken = default)
    {
        if (gameIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT candidate.game_id,
                   game_enrichment.game_id IS NULL OR NOT game_enrichment.rawg_enriched,
                   game_enrichment.game_id IS NULL OR NOT game_enrichment.opencritic_enriched,
                   game_enrichment.game_id IS NULL OR NOT game_enrichment.psn_enriched
            FROM unnest(@game_ids::uuid[]) AS candidate(game_id)
            LEFT JOIN game_enrichment ON game_enrichment.game_id = candidate.game_id
            WHERE game_enrichment.game_id IS NULL
               OR NOT game_enrichment.rawg_enriched
               OR NOT game_enrichment.opencritic_enriched
               OR NOT game_enrichment.psn_enriched
            """;
        cmd.AddParam(CuratorSqlParameters.GameIds, gameIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var needs = new List<EnrichmentNeed>();
        while (await reader.ReadAsync(cancellationToken))
        {
            needs.Add(new EnrichmentNeed(
                reader.GetGuid(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3)));
        }

        return needs;
    }

    public async Task<List<ActiveGenre>> GetActiveGenresAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT genre_id, name, priority FROM genres WHERE active = true";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var genres = new List<ActiveGenre>();
        while (await reader.ReadAsync(cancellationToken))
        {
            genres.Add(new ActiveGenre(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return genres;
    }

    public async Task SaveGameEnrichmentAsync(
        Guid gameId,
        Guid? genreId,
        Guid? subgenreId,
        GameEnrichmentSignals signals,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SaveGameEnrichmentSql;
        cmd.AddParam(CuratorSqlParameters.GameId, gameId);
        cmd.AddParam(CuratorSqlParameters.GenreId, genreId);
        cmd.AddParam(CuratorSqlParameters.SubgenreId, subgenreId);
        cmd.AddParam(CuratorSqlParameters.ReleaseYear, signals.ReleaseYear);
        cmd.AddParam(CuratorSqlParameters.Developer, signals.Developer);
        cmd.AddParam(CuratorSqlParameters.Publisher, signals.Publisher);
        cmd.AddParam(CuratorSqlParameters.Esrb, signals.Esrb);
        cmd.AddParam(CuratorSqlParameters.Multiplayer, signals.Multiplayer);
        cmd.AddParam(CuratorSqlParameters.CriticalScore, signals.CriticalScore);
        cmd.AddParam(CuratorSqlParameters.OcScore, signals.OcScore);
        cmd.AddParam(CuratorSqlParameters.OcTier, signals.OcTier);
        cmd.AddParam(CuratorSqlParameters.OcPercentRecommended, signals.OcPercentRecommended);
        cmd.AddParam(CuratorSqlParameters.PsnRating, signals.PsnRating);
        cmd.AddParam(CuratorSqlParameters.PsnRatingCount, signals.PsnRatingCount);
        cmd.AddParam(CuratorSqlParameters.ScoreSource, signals.ScoreSource);
        cmd.AddParam(CuratorSqlParameters.AaaTier, signals.AaaTier);
        cmd.AddParam(CuratorSqlParameters.RawgEnriched, signals.RawgEnriched);
        cmd.AddParam(CuratorSqlParameters.OpencriticEnriched, signals.OpencriticEnriched);
        cmd.AddParam(CuratorSqlParameters.PsnEnriched, signals.PsnEnriched);
        cmd.AddParam(CuratorSqlParameters.RawgAttempted, signals.RawgAttempted);
        cmd.AddParam(CuratorSqlParameters.OpencriticAttempted, signals.OpencriticAttempted);
        cmd.AddParam(CuratorSqlParameters.PsnAttempted, signals.PsnAttempted);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<PublisherTierRule>> ListPublisherTierRulesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT tier_id, pattern, tier, match_kind FROM publisher_tiers";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rules = new List<PublisherTierRule>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new PublisherTierRule(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return rules;
    }

    public async Task<string?> GetPublisherTierRulesFingerprintAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rules_fingerprint FROM curation_rule_pass_state WHERE pass_name = @pass_name";
        cmd.AddParam(CuratorSqlParameters.PassName, CurationPassNames.TierReclassification);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : value.ToString();
    }

    public async Task SetPublisherTierRulesFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO curation_rule_pass_state (pass_name, rules_fingerprint)
            VALUES (@pass_name, @fingerprint)
            ON CONFLICT (pass_name) DO UPDATE SET
                rules_fingerprint = EXCLUDED.rules_fingerprint, last_ran_at = now()
            """;
        cmd.AddParam(CuratorSqlParameters.PassName, CurationPassNames.TierReclassification);
        cmd.AddParam(CuratorSqlParameters.Fingerprint, fingerprint);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ReclassifyTierAsync(
        IReadOnlyList<PublisherTierRule> rules,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var tierRules = PublisherTierRuleSet.Prepare(rules);
        var changedGameIds = new List<Guid>();
        var changedTiers = new List<string?>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT game_id, publisher, developer, aaa_tier FROM game_enrichment
                WHERE publisher IS NOT NULL OR developer IS NOT NULL
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var publisher = reader.IsDBNull(1) ? null : reader.GetString(1);
                var developer = reader.IsDBNull(2) ? null : reader.GetString(2);
                var storedTier = reader.IsDBNull(3) ? null : reader.GetString(3);
                var newTier = tierRules.ClassifyTier(publisher) ?? tierRules.ClassifyTier(developer);
                if (string.Equals(newTier, storedTier, StringComparison.Ordinal))
                {
                    continue;
                }

                changedGameIds.Add(reader.GetGuid(0));
                changedTiers.Add(newTier);
            }
        }

        if (changedGameIds.Count == 0)
        {
            return 0;
        }

        await using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = """
            UPDATE game_enrichment SET aaa_tier = changed.aaa_tier
            FROM unnest(@game_ids, @aaa_tiers) AS changed (game_id, aaa_tier)
            WHERE game_enrichment.game_id = changed.game_id
            """;
        updateCmd.AddParam(CuratorSqlParameters.GameIds, changedGameIds.ToArray());
        updateCmd.AddParam(CuratorSqlParameters.AaaTiers, changedTiers.ToArray());
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        return changedGameIds.Count;
    }
}
