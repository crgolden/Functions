namespace Functions.Tests.Unit;

internal static class PostgresConnectionStringFixtureConstants
{
    internal const int DefaultPostgresPort = 5432;
    internal const string LibpqDisable = "disable";
    internal const string LibpqAllow = "allow";
    internal const string LibpqPrefer = "prefer";
    internal const string LibpqRequire = "require";
    internal const string LibpqVerifyCa = "verify-ca";
    internal const string LibpqVerifyFull = "verify-full";
    internal const string LibpqSslRootCertParameter = "sslrootcert";
    internal const string LibpqSystemRootCert = "system";
}
