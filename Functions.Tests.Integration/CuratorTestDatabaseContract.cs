namespace Functions.Tests.Integration;

public static class CuratorTestDatabaseContract
{
    public static bool IsDisposableDatabase(string? database) =>
        database is not null
        && (database.EndsWith(CuratorTestDatabaseContractConstants.TestDatabaseNameSuffix, StringComparison.OrdinalIgnoreCase)
            || database.EndsWith(CuratorTestDatabaseContractConstants.TriageDatabaseNameSuffix, StringComparison.OrdinalIgnoreCase));
}
