namespace Functions.Curator.Catalog;

using System.Data.Common;
using System.Globalization;
using Enrichment;
using Functions.Extensions;

public sealed class CatalogRepository
{
    private readonly DbDataSource _dataSource;

    public CatalogRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public async Task<List<FranchiseRule>> ListFranchiseRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rule_id, pattern, franchise, priority FROM franchise_rules";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rules = new List<FranchiseRule>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new FranchiseRule(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return rules;
    }

    public async Task<List<ExclusionRule>> ListExclusionRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rule_id, rule_type, pattern FROM exclusion_rules";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rules = new List<ExclusionRule>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new ExclusionRule(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return rules;
    }

    public async Task<Dictionary<string, int>> GetEditionRanksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT keyword, rank FROM edition_ranks";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            ranks[reader.GetString(0)] = reader.GetInt32(1);
        }

        return ranks;
    }

    public async Task<Dictionary<string, string>> GetNameOverridesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT concept_id, override_name FROM game_name_overrides";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            overrides[reader.GetString(0)] = reader.GetString(1);
        }

        return overrides;
    }

    public async Task<HashSet<string>> GetGloballyExcludedConceptIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT concept_id FROM global_exclusions";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var conceptIds = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            conceptIds.Add(reader.GetString(0));
        }

        return conceptIds;
    }

    public async Task<List<CatalogGame>> ListAllGameIdsAndTitlesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT g.game_id,
                   g.canonical_title,
                   COALESCE(
                       (SELECT c.title_id FROM psn_catalog_cache c
                         WHERE c.game_id = g.game_id ORDER BY c.title_id LIMIT 1),
                       (SELECT l.title_id FROM library_entries l
                         WHERE l.game_id = g.game_id AND l.title_id IS NOT NULL
                         ORDER BY l.title_id LIMIT 1)
                   )
            FROM games g
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var games = new List<CatalogGame>();
        while (await reader.ReadAsync(cancellationToken))
        {
            games.Add(new CatalogGame(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return games;
    }

    public async Task<int> ReclassifyFranchiseAsync(
        IReadOnlyList<FranchiseRule> rules,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = new List<(object GameId, string CanonicalTitle, string? Franchise)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT game_id, canonical_title, franchise FROM games";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetValue(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var updated = 0;
        foreach (var row in rows)
        {
            var newFranchise = FranchiseAssigner.AssignFranchise(row.CanonicalTitle, rules);
            if (string.Equals(newFranchise, row.Franchise, StringComparison.Ordinal))
            {
                continue;
            }

            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText =
                "UPDATE games SET franchise = @franchise, updated_at = now() WHERE game_id = @game_id";
            updateCmd.AddParam("@franchise", newFranchise);
            updateCmd.AddParam("@game_id", row.GameId);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            updated++;
        }

        return updated;
    }

    public async Task<string?> GetFranchiseRulesFingerprintAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT rules_fingerprint FROM curation_rule_pass_state WHERE pass_name = 'franchise_reclassification'";
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : value.ToString();
    }

    public async Task SetFranchiseRulesFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO curation_rule_pass_state (pass_name, rules_fingerprint)
            VALUES ('franchise_reclassification', @fingerprint)
            ON CONFLICT (pass_name) DO UPDATE SET
                rules_fingerprint = EXCLUDED.rules_fingerprint, last_ran_at = now()
            """;
        cmd.AddParam("@fingerprint", fingerprint);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string> UpsertGameAsync(CanonicalGame game, CancellationToken cancellationToken = default)
    {
        var normalizedTitle = game.CanonicalTitle.Trim().ToLowerInvariant();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await AdvisoryLockHandle.HoldUntilCommitAsync(
            connection, transaction, CuratorAdvisoryLocks.GameUpsert, normalizedTitle, cancellationToken);

        string? gameId = null;
        if (game.ConceptIds.Count > 0)
        {
            await using var byConcept = connection.CreateCommand();
            byConcept.Transaction = transaction;
            byConcept.CommandText =
                "SELECT game_id FROM game_concepts WHERE concept_id = ANY(@concept_ids::text[]) LIMIT 1";
            byConcept.AddParam("@concept_ids", game.ConceptIds.ToArray());
            gameId = (await byConcept.ExecuteScalarAsync(cancellationToken))?.ToString();
        }

        if (gameId is null)
        {
            await using var byTitle = connection.CreateCommand();
            byTitle.Transaction = transaction;
            byTitle.CommandText = "SELECT game_id FROM games WHERE normalized_title = @normalized_title";
            byTitle.AddParam("@normalized_title", normalizedTitle);
            gameId = (await byTitle.ExecuteScalarAsync(cancellationToken))?.ToString();
        }

        var franchise = string.IsNullOrWhiteSpace(game.Franchise) ? null : game.Franchise;
        if (gameId is null)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO games (canonical_title, normalized_title, franchise)
                VALUES (@canonical_title, @normalized_title, @franchise)
                RETURNING game_id
                """;
            insert.AddParam("@canonical_title", game.CanonicalTitle);
            insert.AddParam("@normalized_title", normalizedTitle);
            insert.AddParam("@franchise", franchise);
            gameId = (await insert.ExecuteScalarAsync(cancellationToken))?.ToString()
                ?? throw new InvalidOperationException("Inserting a game returned no game_id.");
        }
        else
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE games SET canonical_title = @canonical_title, franchise = @franchise, updated_at = now() WHERE game_id = @game_id";
            update.AddParam("@canonical_title", game.CanonicalTitle);
            update.AddParam("@franchise", franchise);
            update.AddParam("@game_id", Guid.Parse(gameId));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var conceptId in game.ConceptIds)
        {
            await using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = """
                INSERT INTO game_concepts (concept_id, game_id, product_id)
                VALUES (@concept_id, @game_id, @product_id)
                ON CONFLICT (concept_id) DO UPDATE SET
                    game_id = EXCLUDED.game_id,
                    product_id = EXCLUDED.product_id
                """;
            link.AddParam("@concept_id", conceptId);
            link.AddParam("@game_id", Guid.Parse(gameId));
            link.AddParam("@product_id", game.ProductId);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return gameId;
    }
}
