namespace Functions.Curator;

using System.Data.Common;
using Functions.Extensions;

public sealed class AccountActionLogRepository
{
    public const string EnrichmentKeyRejected = "enrichment_key_rejected";

    public const string LibraryRefreshRequested = "library_refresh_requested";

    private readonly DbDataSource _dataSource;

    public AccountActionLogRepository(DbDataSource dataSource) => _dataSource = dataSource;

    public async Task LogAsync(
        string identitySub,
        string action,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO account_action_log (identity_sub, action, detail)
            VALUES (@identity_sub, @action, @detail)
            """;
        cmd.AddParam("@identity_sub", Guid.Parse(identitySub));
        cmd.AddParam("@action", action);
        cmd.AddParam("@detail", detail);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
