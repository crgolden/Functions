namespace Functions.Curator.Library;

using System.Data.Common;
using System.Text.Json;
using Functions.Extensions;

public sealed class LibraryRepository
{
    private const string UpsertEntriesSql = """
        INSERT INTO library_entries (
            identity_sub, game_id, native_ps5, ps4_eligible, owned_edition,
            winning_entitlement_id, product_id, title_id, is_active, source, last_seen_at
        )
        SELECT @identity_sub, s.game_id, s.native_ps5, s.ps4_eligible, s.owned_edition,
               s.winning_entitlement_id, s.product_id, s.title_id, s.is_active, 'psn', now()
        FROM jsonb_to_recordset(@batch::jsonb) AS s(
            game_id uuid, native_ps5 boolean, ps4_eligible boolean, owned_edition text,
            winning_entitlement_id text, product_id text, title_id text, is_active boolean
        )
        ON CONFLICT (identity_sub, game_id) DO UPDATE SET
            native_ps5 = EXCLUDED.native_ps5,
            ps4_eligible = EXCLUDED.ps4_eligible,
            owned_edition = EXCLUDED.owned_edition,
            winning_entitlement_id = EXCLUDED.winning_entitlement_id,
            product_id = EXCLUDED.product_id,
            title_id = EXCLUDED.title_id,
            is_active = EXCLUDED.is_active,
            source = 'psn',
            last_seen_at = now()
        """;

    private const string DeleteUnownedPlatformsSql = """
        DELETE FROM library_entry_platforms p
        USING jsonb_to_recordset(@batch::jsonb) AS s(game_id uuid, platforms text[])
        WHERE p.identity_sub = @identity_sub
          AND p.game_id = s.game_id
          AND NOT (p.platform = ANY(s.platforms))
        """;

    private const string InsertOwnedPlatformsSql = """
        INSERT INTO library_entry_platforms (identity_sub, game_id, platform)
        SELECT @identity_sub, s.game_id, platform
        FROM jsonb_to_recordset(@batch::jsonb) AS s(game_id uuid, platforms text[]),
             unnest(s.platforms) AS platform
        ON CONFLICT DO NOTHING
        """;

    private const string RefreshTrophyProgressSql = """
        UPDATE library_entries e
        SET trophy_percent_completed = s.percent,
            trophy_progress_fetched_at = now()
        FROM jsonb_to_recordset(@batch::jsonb) AS s(np_communication_id text, percent integer)
        WHERE e.identity_sub = @identity_sub
          AND e.np_communication_id = s.np_communication_id
        """;

    private static readonly JsonSerializerOptions BatchFormat = new();

    private readonly DbDataSource _dataSource;

    public LibraryRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public Task UpsertEntryAsync(
        string identitySub,
        string gameId,
        bool nativePs5,
        bool ps4Eligible,
        string? ownedEdition,
        string winningEntitlementId,
        string? productId,
        string? titleId,
        bool isActive = true,
        CancellationToken cancellationToken = default) =>
        UpsertEntryAsync(
            identitySub,
            gameId,
            nativePs5,
            ps4Eligible,
            ownedEdition,
            winningEntitlementId,
            productId,
            titleId,
            [],
            isActive,
            cancellationToken);

    public async Task UpsertEntryAsync(
        string identitySub,
        string gameId,
        bool nativePs5,
        bool ps4Eligible,
        string? ownedEdition,
        string winningEntitlementId,
        string? productId,
        string? titleId,
        IReadOnlyList<string> platforms,
        bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        var entry = LibraryEntryRow.Create(
            gameId,
            nativePs5,
            ps4Eligible,
            ownedEdition,
            winningEntitlementId,
            productId,
            titleId,
            platforms,
            isActive);
        await UpsertEntriesAsync(identitySub, [entry], cancellationToken);
    }

    public async Task UpsertEntriesAsync(
        string identitySub,
        IReadOnlyList<LibraryEntryRow> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var identity = Guid.Parse(identitySub);
        var lastEntryPerGame = new Dictionary<Guid, LibraryEntryRow>(entries.Count);
        foreach (var entry in entries)
        {
            lastEntryPerGame[entry.GameId] = entry;
        }

        List<LibraryEntryRow> uniqueEntries = [.. lastEntryPerGame.Values];
        var batch = JsonSerializer.Serialize(uniqueEntries, BatchFormat);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = UpsertEntriesSql;
            cmd.AddParam("@identity_sub", identity);
            cmd.AddParam("@batch", batch);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = DeleteUnownedPlatformsSql;
            cmd.AddParam("@identity_sub", identity);
            cmd.AddParam("@batch", batch);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = InsertOwnedPlatformsSql;
            cmd.AddParam("@identity_sub", identity);
            cmd.AddParam("@batch", batch);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<List<string>> GetUnmatchedGameIdsAsync(
        string identitySub,
        IReadOnlyList<string> gameIds,
        CancellationToken cancellationToken = default)
    {
        var unmatched = new List<string>();
        if (gameIds.Count == 0)
        {
            return unmatched;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_id FROM library_entries
            WHERE identity_sub = @identity_sub AND game_id = ANY(@game_ids::uuid[]) AND np_communication_id IS NULL
            """;
        cmd.AddParam("@identity_sub", Guid.Parse(identitySub));
        cmd.AddParam("@game_ids", gameIds.Select(Guid.Parse).ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            unmatched.Add(reader.GetGuid(0).ToString());
        }

        return unmatched;
    }

    public async Task<List<ContinuationGame>> GetGamesForContinuationAsync(
        string identitySub,
        IReadOnlyList<string> gameIds,
        CancellationToken cancellationToken = default)
    {
        var games = new List<ContinuationGame>();
        if (gameIds.Count == 0)
        {
            return games;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT le.game_id, g.canonical_title, le.product_id, le.title_id, le.native_ps5
            FROM library_entries le
            JOIN games g ON g.game_id = le.game_id
            WHERE le.identity_sub = @identity_sub AND le.game_id = ANY(@game_ids::uuid[])
            """;
        cmd.AddParam("@identity_sub", Guid.Parse(identitySub));
        cmd.AddParam("@game_ids", gameIds.Select(Guid.Parse).ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            games.Add(new ContinuationGame(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4)));
        }

        return games;
    }

    public async Task SetTrophyMatchAsync(
        string identitySub,
        string gameId,
        string? npCommunicationId,
        string? method,
        int? percentCompleted = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE library_entries
            SET np_communication_id = @np_communication_id,
                trophy_match_method = @method,
                trophy_match_attempted_at = now(),
                trophy_percent_completed = @percent_completed,
                trophy_progress_fetched_at = CASE WHEN @percent_completed::smallint IS NULL
                    THEN trophy_progress_fetched_at ELSE now() END
            WHERE identity_sub = @identity_sub AND game_id = @game_id
            """;
        cmd.AddParam("@np_communication_id", npCommunicationId);
        cmd.AddParam("@method", method);
        cmd.AddParam("@percent_completed", percentCompleted);
        cmd.AddParam("@identity_sub", Guid.Parse(identitySub));
        cmd.AddParam("@game_id", Guid.Parse(gameId));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RefreshTrophyProgressAsync(
        string identitySub,
        IReadOnlyDictionary<string, int> progressByNpId,
        CancellationToken cancellationToken = default)
    {
        if (progressByNpId.Count == 0)
        {
            return 0;
        }

        var rows = progressByNpId
            .Select(pair => new TrophyProgressRow { NpCommunicationId = pair.Key, Percent = pair.Value })
            .ToList();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = RefreshTrophyProgressSql;
        cmd.AddParam("@identity_sub", Guid.Parse(identitySub));
        cmd.AddParam("@batch", JsonSerializer.Serialize(rows, BatchFormat));
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
