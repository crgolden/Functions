namespace Functions.Tests.Integration;

using System.Data.Common;
using Npgsql;

public sealed class CuratorDatabase : IAsyncLifetime
{
    public const string ConnectionVariable = "CuratorTestDatabaseConnection";

    public const string TestPublisherTierPattern = "integration-test-pattern";

    private const string SchemaProbeSql =
        "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'entitlement_snapshots'";

    private const string InsertUserSql = "INSERT INTO app_users (identity_sub) VALUES ($1)";

    private const string DeleteUserSql = "DELETE FROM app_users WHERE identity_sub = $1";

    private const string DeleteTestTiersSql = "DELETE FROM publisher_tiers WHERE pattern LIKE $1";

    private static readonly string[] UnseededTablesChildFirst =
    [
        "app_users",
        "game_name_overrides",
        "global_exclusions",
        "game_concepts",
        "game_enrichment",
        "psn_catalog_cache",
        "games",
        "rawg_cache",
        "opencritic_cache",
        "exclusion_rules",
        "edition_ranks",
        "curation_rule_pass_state",
    ];

    private NpgsqlDataSource? _dataSource;

    public DbDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{ConnectionVariable} is not set. The integration tier connects to an existing Curator database and never creates or migrates one; point it at a test database whose schema Curator has already migrated. See Functions/TESTING.md.");
        }

        _dataSource = NpgsqlDataSource.Create(PostgresConnectionString.Normalize(configured));

        var found = await ScalarAsync<long>(SchemaProbeSql, CancellationToken.None);
        if (found is not 1)
        {
            throw new InvalidOperationException(
                $"The database named by {ConnectionVariable} has no 'entitlement_snapshots' table. Curator owns this schema — run its migrations against the target database rather than creating tables here.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is null)
        {
            return;
        }

        await SweepRowsOrphanedByAFailedTestAsync();
        await _dataSource.DisposeAsync();
    }

    public async Task<Guid> CreateUserAsync(CancellationToken cancellationToken)
    {
        var identitySub = Guid.NewGuid();
        await ExecuteAsync(InsertUserSql, cancellationToken, identitySub);
        return identitySub;
    }

    public async Task DeleteUserAsync(Guid identitySub, CancellationToken cancellationToken) =>
        await ExecuteAsync(DeleteUserSql, cancellationToken, identitySub);

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken, params object[] arguments)
    {
        await using var command = CreateCommand(sql, arguments);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is T typed
            ? typed
            : throw new InvalidOperationException($"Expected {typeof(T).Name}, got {value?.GetType().Name ?? "null"} from: {sql}");
    }

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params object[] arguments)
    {
        await using var command = CreateCommand(sql, arguments);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SweepRowsOrphanedByAFailedTestAsync()
    {
        if (!TargetsATestDatabase())
        {
            return;
        }

        foreach (var table in UnseededTablesChildFirst)
        {
            await ExecuteAsync($"DELETE FROM {table}", CancellationToken.None);
        }

        await ExecuteAsync(DeleteTestTiersSql, CancellationToken.None, $"{TestPublisherTierPattern}%");
    }

    private bool TargetsATestDatabase()
    {
        var database = new NpgsqlConnectionStringBuilder(_dataSource?.ConnectionString).Database;
        return database is not null
            && database.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    private NpgsqlCommand CreateCommand(string sql, params object[] arguments)
    {
        var command = _dataSource is null
            ? throw new InvalidOperationException("The fixture has not been initialized.")
            : _dataSource.CreateCommand(sql);

        foreach (var argument in arguments)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = argument });
        }

        return command;
    }
}
