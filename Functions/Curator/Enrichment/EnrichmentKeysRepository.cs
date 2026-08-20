namespace Functions.Curator.Enrichment;

using System.Data.Common;
using Functions.Extensions;

public sealed class EnrichmentKeysRepository
{
    private readonly DbDataSource _dataSource;

    public EnrichmentKeysRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public async Task<(byte[]? RawgKeyEnc, byte[]? OpenCriticKeyEnc)> GetDecryptedKeyMaterialAsync(
        string identitySub,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT rawg_api_key_enc, opencritic_api_key_enc FROM user_enrichment_keys WHERE identity_sub = @identity_sub";
        cmd.AddParam("@identity_sub", Guid.Parse(identitySub));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, null);
        }

        return (
            reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0),
            reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1));
    }

    public async Task MarkRawgKeyRejectedAsync(string identitySub, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE user_enrichment_keys SET rawg_key_rejected_at = now() WHERE identity_sub = @identity_sub";
        cmd.AddParam("@identity_sub", Guid.Parse(identitySub));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOpenCriticKeyRejectedAsync(string identitySub, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE user_enrichment_keys SET opencritic_key_rejected_at = now() WHERE identity_sub = @identity_sub";
        cmd.AddParam("@identity_sub", Guid.Parse(identitySub));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
