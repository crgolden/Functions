namespace Functions.Curator.Enrichment;

using System.Data.Common;
using Functions.Extensions;
using OpenCritic;
using Rawg;

public sealed class EnrichmentRepository
{
    private readonly DbDataSource _dataSource;

    public EnrichmentRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public Task<AdvisoryLockHandle> TryLockCatalogEnrichmentPassAsync(CancellationToken cancellationToken = default) =>
        AdvisoryLockHandle.TryAcquireAsync(
            _dataSource, CuratorAdvisoryLocks.EnrichmentRun, "catalog_enrichment_pass", cancellationToken);

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
        cmd.AddParam("@normalized_title", normalizedTitle);
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
        cmd.AddParam("@normalized_title", normalizedTitle);
        cmd.AddParam("@rawg_game_id", rawgGameId);
        cmd.AddParam("@raw", raw);
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
                   content_rating, rating_authority, multiplayer, concept_fetched_at
            FROM psn_catalog_cache WHERE title_id = @title_id
            """;
        cmd.AddParam("@title_id", titleId);
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
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
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
                                            rating_authority, multiplayer, concept_fetched_at)
            VALUES (@title_id, @concept_id, @genres, @star_rating, @publisher, @release_date::date,
                    @cover_image_url, @content_rating, @rating_authority, @multiplayer, now())
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
                fetched_at = now()
            """;
        cmd.AddParam("@title_id", entry.TitleId);
        cmd.AddParam("@concept_id", entry.ConceptId);
        cmd.AddParam("@genres", entry.Genres.ToArray());
        cmd.AddParam("@star_rating", entry.StarRating);
        cmd.AddParam("@publisher", entry.Publisher);
        cmd.AddParam("@release_date", entry.ReleaseDate);
        cmd.AddParam("@cover_image_url", entry.CoverImageUrl);
        cmd.AddParam("@content_rating", entry.ContentRating);
        cmd.AddParam("@rating_authority", entry.RatingAuthority);
        cmd.AddParam("@multiplayer", entry.Multiplayer);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<string>> GetUnenrichedGameIdsAsync(
        IReadOnlyCollection<string> gameIds,
        CancellationToken cancellationToken = default)
    {
        if (gameIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_id FROM unnest(@game_ids::uuid[]) AS candidate(game_id)
            WHERE NOT EXISTS (
                SELECT 1 FROM game_enrichment WHERE game_enrichment.game_id = candidate.game_id
            )
            """;
        cmd.AddParam("@game_ids", gameIds.Select(Guid.Parse).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var unenriched = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            unenriched.Add(reader.GetGuid(0).ToString());
        }

        return unenriched;
    }

    public async Task<List<string>> GetGameIdsNeverAskedOfRawgAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT game_id FROM game_enrichment WHERE rawg_attempted_at IS NULL";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var gameIds = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            gameIds.Add(reader.GetGuid(0).ToString());
        }

        return gameIds;
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
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return genres;
    }

    public async Task SaveGameEnrichmentAsync(
        string gameId,
        string? genreId,
        string? subgenreId,
        GameEnrichmentSignals signals,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_enrichment (
                game_id, genre_id, subgenre_id, release_year, developer, publisher, esrb, multiplayer,
                critical_score, oc_score, oc_tier, oc_percent_recommended, psn_rating, score_source,
                aaa_tier, rawg_enriched, opencritic_enriched, rawg_attempted_at
            )
            VALUES (@game_id, @genre_id, @subgenre_id, @release_year, @developer, @publisher, @esrb,
                    @multiplayer, @critical_score, @oc_score, @oc_tier, @oc_percent_recommended,
                    @psn_rating, @score_source, @aaa_tier, @rawg_enriched, @opencritic_enriched,
                    CASE WHEN @rawg_attempted THEN now() END)
            ON CONFLICT (game_id) DO UPDATE SET
                genre_id = EXCLUDED.genre_id,
                subgenre_id = EXCLUDED.subgenre_id,
                release_year = EXCLUDED.release_year,
                developer = CASE
                    WHEN @rawg_attempted THEN EXCLUDED.developer
                    ELSE COALESCE(EXCLUDED.developer, game_enrichment.developer)
                END,
                publisher = EXCLUDED.publisher,
                esrb = EXCLUDED.esrb,
                multiplayer = EXCLUDED.multiplayer,
                critical_score = CASE
                    WHEN @rawg_attempted THEN EXCLUDED.critical_score
                    ELSE COALESCE(EXCLUDED.critical_score, game_enrichment.critical_score)
                END,
                oc_score = EXCLUDED.oc_score,
                oc_tier = EXCLUDED.oc_tier,
                oc_percent_recommended = EXCLUDED.oc_percent_recommended,
                psn_rating = EXCLUDED.psn_rating,
                score_source = CASE
                    WHEN @rawg_attempted THEN EXCLUDED.score_source
                    ELSE COALESCE(EXCLUDED.score_source, game_enrichment.score_source)
                END,
                aaa_tier = EXCLUDED.aaa_tier,
                rawg_enriched = EXCLUDED.rawg_enriched,
                opencritic_enriched = EXCLUDED.opencritic_enriched,
                rawg_attempted_at = CASE
                    WHEN @rawg_attempted THEN now()
                    ELSE game_enrichment.rawg_attempted_at
                END,
                enriched_at = now()
            """;
        cmd.AddParam("@game_id", Guid.Parse(gameId));
        cmd.AddParam("@genre_id", genreId is null ? null : Guid.Parse(genreId));
        cmd.AddParam("@subgenre_id", subgenreId is null ? null : Guid.Parse(subgenreId));
        cmd.AddParam("@release_year", signals.ReleaseYear);
        cmd.AddParam("@developer", signals.Developer);
        cmd.AddParam("@publisher", signals.Publisher);
        cmd.AddParam("@esrb", signals.Esrb);
        cmd.AddParam("@multiplayer", signals.Multiplayer);
        cmd.AddParam("@critical_score", signals.CriticalScore);
        cmd.AddParam("@oc_score", signals.OcScore);
        cmd.AddParam("@oc_tier", signals.OcTier);
        cmd.AddParam("@oc_percent_recommended", signals.OcPercentRecommended);
        cmd.AddParam("@psn_rating", signals.PsnRating);
        cmd.AddParam("@score_source", signals.ScoreSource);
        cmd.AddParam("@aaa_tier", signals.AaaTier);
        cmd.AddParam("@rawg_enriched", signals.RawgEnriched);
        cmd.AddParam("@opencritic_enriched", signals.OpencriticEnriched);
        cmd.AddParam("@rawg_attempted", signals.RawgAttempted);
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
        cmd.AddParam("@pass_name", CurationPassNames.TierReclassification);
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
        cmd.AddParam("@pass_name", CurationPassNames.TierReclassification);
        cmd.AddParam("@fingerprint", fingerprint);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ReclassifyTierAsync(
        IReadOnlyList<PublisherTierRule> rules,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = new List<(object GameId, string? Publisher, string? Developer, string? AaaTier)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT game_id, publisher, developer, aaa_tier FROM game_enrichment
                WHERE publisher IS NOT NULL OR developer IS NOT NULL
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetValue(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var tierRules = PublisherTierRuleSet.Prepare(rules);
        var updated = 0;
        foreach (var row in rows)
        {
            var newTier = tierRules.ClassifyTier(row.Publisher) ?? tierRules.ClassifyTier(row.Developer);
            if (string.Equals(newTier, row.AaaTier, StringComparison.Ordinal))
            {
                continue;
            }

            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = "UPDATE game_enrichment SET aaa_tier = @aaa_tier WHERE game_id = @game_id";
            updateCmd.AddParam("@aaa_tier", newTier);
            updateCmd.AddParam("@game_id", row.GameId);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            updated++;
        }

        return updated;
    }
}
