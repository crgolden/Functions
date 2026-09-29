namespace Functions.Curator.Enrichment;

using System.Data.Common;
using Functions.Extensions;

public sealed class EnrichmentKeysRepository
{
    private readonly DbDataSource _dataSource;

    public EnrichmentKeysRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyDictionary<EnrichmentProvider, byte[]>> GetEncryptedKeysAsync(
        Guid identitySub,
        CancellationToken cancellationToken = default)
    {
        var keys = new Dictionary<EnrichmentProvider, byte[]>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT rawg_api_key_enc, opencritic_api_key_enc FROM user_enrichment_keys WHERE identity_sub = @identity_sub";
        cmd.AddParam(CuratorSqlParameters.IdentitySub, identitySub);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return keys;
        }

        if (!reader.IsDBNull(0))
        {
            keys[EnrichmentProvider.Rawg] = (byte[])reader.GetValue(0);
        }

        if (!reader.IsDBNull(1))
        {
            keys[EnrichmentProvider.OpenCritic] = (byte[])reader.GetValue(1);
        }

        return keys;
    }

    public async Task MarkRawgKeyRejectedAsync(Guid identitySub, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE user_enrichment_keys SET rawg_key_rejected_at = now() WHERE identity_sub = @identity_sub";
        cmd.AddParam(CuratorSqlParameters.IdentitySub, identitySub);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOpenCriticKeyRejectedAsync(Guid identitySub, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE user_enrichment_keys SET opencritic_key_rejected_at = now() WHERE identity_sub = @identity_sub";
        cmd.AddParam(CuratorSqlParameters.IdentitySub, identitySub);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
