namespace Functions.Curator.Psn;

using System.Data.Common;
using Functions.Extensions;

public sealed class PsnLinkRepository
{
    private readonly DbDataSource _dataSource;

    public PsnLinkRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public async Task<PsnLink?> GetLinkAsync(Guid identitySub, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT token_response_enc, harvest_trophies FROM psn_links WHERE identity_sub = @identity_sub";
        cmd.AddParam(CuratorSqlParameters.IdentitySub, identitySub);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PsnLink((byte[])reader.GetValue(0), reader.GetBoolean(1));
    }

    public async Task<bool> UpdateTokenAsync(
        Guid identitySub,
        byte[] tokenResponseEnc,
        double? accessTokenExpiresAt,
        double? refreshTokenExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE psn_links
            SET token_response_enc = @token_response_enc,
                access_token_expires_at = @access_token_expires_at,
                refresh_token_expires_at = @refresh_token_expires_at,
                updated_at = now()
            WHERE identity_sub = @identity_sub
            """;
        cmd.AddParam(CuratorSqlParameters.TokenResponseEnc, tokenResponseEnc);
        cmd.AddParam(CuratorSqlParameters.AccessTokenExpiresAt, ToTimestamp(accessTokenExpiresAt));
        cmd.AddParam(CuratorSqlParameters.RefreshTokenExpiresAt, ToTimestamp(refreshTokenExpiresAt));
        cmd.AddParam(CuratorSqlParameters.IdentitySub, identitySub);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static DateTimeOffset? ToTimestamp(double? unixSeconds) =>
        unixSeconds is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
}
