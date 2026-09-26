namespace Functions.Tests.Unit;

internal static class PostgresConnectionStringFixtureConstants
{
    internal const int DefaultPostgresPort = 5432;
    internal const string LibpqDisable = PostgresConnectionString.LibpqSslModes.Disable;
    internal const string LibpqAllow = PostgresConnectionString.LibpqSslModes.Allow;
    internal const string LibpqPrefer = PostgresConnectionString.LibpqSslModes.Prefer;
    internal const string LibpqRequire = PostgresConnectionString.LibpqSslModes.Require;
    internal const string LibpqVerifyCa = PostgresConnectionString.LibpqSslModes.VerifyCa;
    internal const string LibpqVerifyFull = PostgresConnectionString.LibpqSslModes.VerifyFull;
    internal const string LibpqSslRootCertParameter = "sslrootcert";
    internal const string LibpqSystemRootCert = "system";
}
