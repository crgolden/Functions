namespace Functions.Curator.Library;

using System.Data.Common;
using System.Text.Json;
using Functions.Curator.Psn;
using Functions.Extensions;

public sealed class LibraryRepository
{
    internal static readonly JsonSerializerOptions BatchFormat = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private const string IdentitySubParameter = CuratorSqlParameters.IdentitySub;

    private const string BatchParameter = CuratorSqlParameters.Batch;

    private const string UpsertEntriesSql = $"""
        INSERT INTO library_entries (
            identity_sub, game_id, owned_edition,
            winning_entitlement_id, product_id, title_id, is_active, source, last_seen_at
        )
        SELECT @identity_sub, s.game_id, s.owned_edition,
               s.winning_entitlement_id, s.product_id, s.title_id, s.is_active, '{LibraryEntrySources.Psn}', now()
        FROM jsonb_to_recordset(@batch::jsonb) AS s(
            game_id uuid, owned_edition text,
            winning_entitlement_id text, product_id text, title_id text, is_active boolean
        )
        ON CONFLICT (identity_sub, game_id) DO UPDATE SET
            owned_edition = EXCLUDED.owned_edition,
            winning_entitlement_id = EXCLUDED.winning_entitlement_id,
            product_id = EXCLUDED.product_id,
            title_id = EXCLUDED.title_id,
            is_active = EXCLUDED.is_active,
            source = '{LibraryEntrySources.Psn}',
            last_seen_at = now()
        WHERE library_entries.source = '{LibraryEntrySources.Psn}'
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

    private const string UpsertDownloadSizesSql = """
        INSERT INTO game_download_sizes (game_id, platform, bytes, fetched_at)
        SELECT DISTINCT ON (le.game_id, s.platform) le.game_id, s.platform, s.bytes, now()
        FROM jsonb_to_recordset(@batch::jsonb) AS s(title_id text, platform text, bytes bigint)
        JOIN library_entries le ON le.identity_sub = @identity_sub AND le.title_id = s.title_id
        ORDER BY le.game_id, s.platform, s.bytes DESC
        ON CONFLICT (game_id, platform) DO UPDATE SET
            bytes = EXCLUDED.bytes,
            fetched_at = now()
        """;

    private const string RefreshTrophyProgressSql = """
        UPDATE library_entries e
        SET trophy_percent_completed = s.percent,
            trophy_progress_fetched_at = now()
        FROM jsonb_to_recordset(@batch::jsonb) AS s(np_communication_id text, percent integer)
        WHERE e.identity_sub = @identity_sub
          AND e.np_communication_id = s.np_communication_id
        """;

    private readonly DbDataSource _dataSource;

    public LibraryRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public Task UpsertEntryAsync(
        Guid identitySub,
        Guid gameId,
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
        Guid identitySub,
        Guid gameId,
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

    public async Task<int> UpsertDownloadSizesAsync(
        Guid identitySub,
        IReadOnlyList<EntitlementDownloadSize> sizes,
        CancellationToken cancellationToken = default)
    {
        if (sizes.Count == 0)
        {
            return 0;
        }

        var batch = JsonSerializer.Serialize(sizes, BatchFormat);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = UpsertDownloadSizesSql;
        cmd.AddParam(IdentitySubParameter, identitySub);
        cmd.AddParam(BatchParameter, batch);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertEntriesAsync(
        Guid identitySub,
        IReadOnlyList<LibraryEntryRow> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var mergedPerGame = new Dictionary<Guid, LibraryEntryRow>(entries.Count);
        foreach (var entry in entries)
        {
            mergedPerGame[entry.GameId] = mergedPerGame.TryGetValue(entry.GameId, out var alreadySeen)
                ? MergeOntoSameGame(alreadySeen, entry)
                : entry;
        }

        List<LibraryEntryRow> uniqueEntries = [.. mergedPerGame.Values];
        var batch = JsonSerializer.Serialize(uniqueEntries, BatchFormat);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = UpsertEntriesSql;
            cmd.AddParam(IdentitySubParameter, identitySub);
            cmd.AddParam(BatchParameter, batch);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = DeleteUnownedPlatformsSql;
            cmd.AddParam(IdentitySubParameter, identitySub);
            cmd.AddParam(BatchParameter, batch);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = InsertOwnedPlatformsSql;
            cmd.AddParam(IdentitySubParameter, identitySub);
            cmd.AddParam(BatchParameter, batch);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<List<Guid>> GetUnmatchedGameIdsAsync(
        Guid identitySub,
        IReadOnlyList<Guid> gameIds,
        CancellationToken cancellationToken = default)
    {
        var unmatched = new List<Guid>();
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
        cmd.AddParam(IdentitySubParameter, identitySub);
        cmd.AddParam(CuratorSqlParameters.GameIds, gameIds.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            unmatched.Add(reader.GetGuid(0));
        }

        return unmatched;
    }

    public async Task<List<ContinuationGame>> GetGamesForContinuationAsync(
        Guid identitySub,
        IReadOnlyList<Guid> gameIds,
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
            SELECT le.game_id, g.canonical_title, le.product_id, le.title_id,
                   EXISTS (
                       SELECT 1 FROM library_entry_platforms lep
                       WHERE lep.identity_sub = le.identity_sub AND lep.game_id = le.game_id
                         AND lep.platform = @ps5_platform
                   ) AS native_ps5
            FROM library_entries le
            JOIN games g ON g.game_id = le.game_id
            WHERE le.identity_sub = @identity_sub AND le.game_id = ANY(@game_ids::uuid[])
            """;
        cmd.AddParam(CuratorSqlParameters.Ps5Platform, TitlePlatform.Ps5);
        cmd.AddParam(IdentitySubParameter, identitySub);
        cmd.AddParam(CuratorSqlParameters.GameIds, gameIds.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            games.Add(new ContinuationGame(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4)));
        }

        return games;
    }

    public async Task SetTrophyMatchAsync(
        Guid identitySub,
        Guid gameId,
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
        cmd.AddParam(CuratorSqlParameters.NpCommunicationId, npCommunicationId);
        cmd.AddParam(CuratorSqlParameters.Method, method);
        cmd.AddParam(CuratorSqlParameters.PercentCompleted, percentCompleted);
        cmd.AddParam(IdentitySubParameter, identitySub);
        cmd.AddParam(CuratorSqlParameters.GameId, gameId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RefreshTrophyProgressAsync(
        Guid identitySub,
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
        cmd.AddParam(IdentitySubParameter, identitySub);
        cmd.AddParam(BatchParameter, JsonSerializer.Serialize(rows, BatchFormat));
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static LibraryEntryRow MergeOntoSameGame(LibraryEntryRow alreadySeen, LibraryEntryRow next) =>
        next with
        {
            Platforms = [.. alreadySeen.Platforms.Union(next.Platforms, StringComparer.Ordinal)],
            IsActive = alreadySeen.IsActive || next.IsActive,
        };
}
