namespace Functions.Curator.Library;

using System.Data.Common;
using System.Text.Json;
using Functions.Extensions;

public sealed class EntitlementPullRepository
{
    private const string InsertPullSql = """
        INSERT INTO entitlement_pulls (identity_sub, source, entry_count)
        VALUES (@identity_sub, @source, @entry_count)
        RETURNING pull_id
        """;

    private const string UpsertSnapshotSql = """
        INSERT INTO entitlement_snapshots (
            identity_sub, pull_id, entitlement_id, concept_id, product_id, sku_id, title_id,
            game_meta_name, concept_meta_name, title_meta_name, package_type, active,
            active_date, title_image_url, game_icon_url, concept_icon_url, is_game,
            platform_ids, raw, first_seen_at, last_seen_at
        )
        SELECT @identity_sub, @pull_id, s.entitlement_id, s.concept_id, s.product_id, s.sku_id, s.title_id,
               s.game_meta_name, s.concept_meta_name, s.title_meta_name, s.package_type, s.active,
               s.active_date, s.title_image_url, s.game_icon_url, s.concept_icon_url, s.is_game,
               s.platform_ids, s.raw, now(), now()
        FROM jsonb_to_recordset(@batch::jsonb) AS s(
            entitlement_id text, concept_id text, product_id text, sku_id text, title_id text,
            game_meta_name text, concept_meta_name text, title_meta_name text, package_type text,
            active boolean, active_date timestamptz, title_image_url text, game_icon_url text,
            concept_icon_url text, is_game boolean, platform_ids text[], raw jsonb
        )
        ON CONFLICT (identity_sub, entitlement_id) DO UPDATE SET
            pull_id = EXCLUDED.pull_id,
            concept_id = EXCLUDED.concept_id,
            product_id = EXCLUDED.product_id,
            sku_id = EXCLUDED.sku_id,
            title_id = EXCLUDED.title_id,
            game_meta_name = EXCLUDED.game_meta_name,
            concept_meta_name = EXCLUDED.concept_meta_name,
            title_meta_name = EXCLUDED.title_meta_name,
            package_type = EXCLUDED.package_type,
            active = EXCLUDED.active,
            active_date = EXCLUDED.active_date,
            title_image_url = COALESCE(EXCLUDED.title_image_url, entitlement_snapshots.title_image_url),
            game_icon_url = COALESCE(EXCLUDED.game_icon_url, entitlement_snapshots.game_icon_url),
            concept_icon_url = COALESCE(EXCLUDED.concept_icon_url, entitlement_snapshots.concept_icon_url),
            is_game = EXCLUDED.is_game,
            platform_ids = EXCLUDED.platform_ids,
            raw = COALESCE(NULLIF(EXCLUDED.raw, '{}'::jsonb), entitlement_snapshots.raw),
            last_seen_at = now()
        """;

    private static readonly JsonSerializerOptions BatchFormat = new();

    private readonly DbDataSource _dataSource;

    public EntitlementPullRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public async Task<string> RecordPullAsync(
        string identitySub,
        string source,
        IReadOnlyCollection<EntitlementSnapshot> snapshots,
        int entryCount,
        CancellationToken cancellationToken = default)
    {
        var identity = Guid.Parse(identitySub);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var pullId = await InsertPullAsync(connection, transaction, identity, source, entryCount, cancellationToken);

        if (snapshots.Count > 0)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = UpsertSnapshotSql;
            cmd.AddParam("@identity_sub", identity);
            cmd.AddParam("@pull_id", pullId);
            cmd.AddParam("@batch", SerializeBatch(snapshots));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return pullId.ToString();
    }

    private static string SerializeBatch(IReadOnlyCollection<EntitlementSnapshot> snapshots) =>
        JsonSerializer.Serialize(
            snapshots.Select(EntitlementSnapshotRow.From).ToList(),
            BatchFormat);

    private static async Task<Guid> InsertPullAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid identity,
        string source,
        int entryCount,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = InsertPullSql;
        cmd.AddParam("@identity_sub", identity);
        cmd.AddParam("@source", source);
        cmd.AddParam("@entry_count", entryCount);

        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        return scalar switch
        {
            Guid pullId => pullId,
            string text => Guid.Parse(text),
            _ => throw new InvalidOperationException("entitlement_pulls INSERT returned no pull_id."),
        };
    }
}
