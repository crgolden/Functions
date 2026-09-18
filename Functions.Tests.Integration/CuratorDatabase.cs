namespace Functions.Tests.Integration;

using System.Data.Common;
using Npgsql;

public sealed class CuratorDatabase : IAsyncLifetime
{
    public const string TestPublisherTierPattern = nameof(CuratorDatabase);

    private const string SchemaProbeSql =
        "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'entitlement_snapshots'";

    private const string InsertUserSql = "INSERT INTO app_users (identity_sub) VALUES ($1)";

    private const string DeleteUserSql = "DELETE FROM app_users WHERE identity_sub = $1";

    private const string DeleteTestTiersSql = "DELETE FROM publisher_tiers WHERE pattern LIKE $1";

    private const string DeleteUnseededRowsChildFirstSql = """
        DELETE FROM app_users;
        DELETE FROM game_name_overrides;
        DELETE FROM global_exclusions;
        DELETE FROM game_concepts;
        DELETE FROM game_enrichment;
        DELETE FROM psn_catalog_cache;
        DELETE FROM games;
        DELETE FROM rawg_cache;
        DELETE FROM opencritic_cache;
        DELETE FROM curation_rule_pass_state;
        """;

    private NpgsqlDataSource? _dataSource;

    public DbDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(CuratorTestDatabaseContractConstants.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{CuratorTestDatabaseContractConstants.ConnectionVariable} is not set. The integration tier connects to an existing Curator database and never creates or migrates one; point it at a test database whose schema Curator has already migrated. See Functions/TESTING.md.");
        }

        _dataSource = NpgsqlDataSource.Create(PostgresConnectionString.Normalize(configured));

        var found = await ScalarAsync<long>(SchemaProbeSql, CancellationToken.None);
        if (found is not 1)
        {
            throw new InvalidOperationException(
                $"The database named by {CuratorTestDatabaseContractConstants.ConnectionVariable} has no 'entitlement_snapshots' table. Curator owns this schema — run its migrations against the target database rather than creating tables here.");
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

    public async Task<T?> ScalarOrDefaultAsync<T>(string sql, CancellationToken cancellationToken, params object[] arguments)
        where T : struct
    {
        await using var command = CreateCommand(sql, arguments);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is T typed ? typed : null;
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

        await ExecuteAsync(DeleteUnseededRowsChildFirstSql, CancellationToken.None);
        await ExecuteAsync(DeleteTestTiersSql, CancellationToken.None, $"{TestPublisherTierPattern}%");
    }

    private bool TargetsATestDatabase()
    {
        var database = new NpgsqlConnectionStringBuilder(_dataSource?.ConnectionString).Database;
        return database is not null
            && database.EndsWith(CuratorTestDatabaseContractConstants.TestDatabaseNameSuffix, StringComparison.OrdinalIgnoreCase);
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
