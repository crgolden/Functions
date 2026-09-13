namespace Functions.Tests.Unit;

using System.Globalization;
using Npgsql;
using TestSupport;
using static PostgresConnectionStringFixtureConstants;
using static TestSupport.TestValues;

[Trait("Category", "Unit")]
public sealed class PostgresConnectionStringTests
{
    [Theory]
    [InlineData("postgresql")]
    [InlineData("postgres")]
    public void Normalize_UriForm_ProducesConnectionStringNpgsqlCanParse(string scheme)
    {
        // Arrange
        var databaseHost = NewHost();
        var databasePort = NewPortNumber();
        var databaseName = NewPostgresIdentifier();
        var databaseUser = NewPostgresIdentifier();
        var databasePassword = NewPostgresIdentifier();

        // Act
        var normalized = PostgresConnectionString.Normalize(
            $"{scheme}://{databaseUser}:{databasePassword}@{databaseHost}:{databasePort.ToString(CultureInfo.InvariantCulture)}/{databaseName}");

        // Assert
        var parsed = new NpgsqlConnectionStringBuilder(normalized);
        Assert.Equal(databaseHost, parsed.Host);
        Assert.Equal(databasePort, parsed.Port);
        Assert.Equal(databaseName, parsed.Database);
        Assert.Equal(databaseUser, parsed.Username);
        Assert.Equal(databasePassword, parsed.Password);
    }

    [Fact]
    public void Normalize_UriWithoutPort_DefaultsToPostgresPort()
    {
        // Arrange
        var databaseUri = $"postgresql://{NewPostgresIdentifier()}:{NewPostgresIdentifier()}@{NewHost()}/{NewPostgresIdentifier()}";

        // Act
        var normalized = PostgresConnectionString.Normalize(databaseUri);

        // Assert
        Assert.Equal(DefaultPostgresPort, new NpgsqlConnectionStringBuilder(normalized).Port);
    }

    [Fact]
    public void Normalize_PercentEncodedPassword_IsDecoded()
    {
        // Arrange
        var decodedPassword = $"{NewPostgresIdentifier()}@{NewPostgresIdentifier()}:{NewPostgresIdentifier()}";
        var encodedPassword = decodedPassword.Replace("@", "%40", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal);
        var databaseUri = $"postgresql://{NewPostgresIdentifier()}:{encodedPassword}@{NewHost()}/{NewPostgresIdentifier()}";

        // Act
        var normalized = PostgresConnectionString.Normalize(databaseUri);

        // Assert
        Assert.Equal(decodedPassword, new NpgsqlConnectionStringBuilder(normalized).Password);
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("allow", SslMode.Allow)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    [InlineData("VERIFY-FULL", SslMode.VerifyFull)]
    public void Normalize_UriCarryingSslMode_MapsLibpqSpellingToNpgsqlSslMode(string libpqSpelling, SslMode expected)
    {
        // Arrange
        var databaseUri = $"{NewUriWithoutQuery()}?{PostgresConnectionString.SslModeParameter}={libpqSpelling}";

        // Act
        var normalized = PostgresConnectionString.Normalize(databaseUri);

        // Assert
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(normalized).SslMode);
    }

    [Fact]
    public void Normalize_UriWithoutSslMode_LeavesNpgsqlOwnDefault()
    {
        // Act
        var normalized = PostgresConnectionString.Normalize(NewUriWithoutQuery());

        // Assert
        Assert.Equal(new NpgsqlConnectionStringBuilder().SslMode, new NpgsqlConnectionStringBuilder(normalized).SslMode);
    }

    [Fact]
    public void Normalize_UriCarryingSslRootCertSystem_DropsItAndKeepsTheSslMode()
    {
        // Arrange
        var databaseUri =
            $"{NewUriWithoutQuery()}?{PostgresConnectionString.SslModeParameter}=verify-full&sslrootcert=system";

        // Act
        var normalized = PostgresConnectionString.Normalize(databaseUri);

        // Assert
        var parsed = new NpgsqlConnectionStringBuilder(normalized);
        Assert.Equal(SslMode.VerifyFull, parsed.SslMode);
        Assert.Null(parsed.RootCertificate);
    }

    [Fact]
    public void Normalize_UriCarryingUnknownSslMode_Throws()
    {
        // Arrange
        var unknownSpelling = NewUnknownSslModeSpelling();
        var databaseUri = $"{NewUriWithoutQuery()}?{PostgresConnectionString.SslModeParameter}={unknownSpelling}";

        // Act
        var exception = Record.Exception(() => PostgresConnectionString.Normalize(databaseUri));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void Normalize_UriCarryingSslModeWithNoValue_Throws()
    {
        // Arrange
        var databaseUri = $"{NewUriWithoutQuery()}?{PostgresConnectionString.SslModeParameter}=";

        // Act
        var exception = Record.Exception(() => PostgresConnectionString.Normalize(databaseUri));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void Normalize_KeywordForm_IsReturnedUnchanged()
    {
        // Arrange
        var keywordForm =
            $"Host={NewHost()};Port={NewPortNumber().ToString(CultureInfo.InvariantCulture)};Database={NewPostgresIdentifier()};Username={NewPostgresIdentifier()};Password={NewPostgresIdentifier()}";

        // Act
        var normalized = PostgresConnectionString.Normalize(keywordForm);

        // Assert
        Assert.Equal(keywordForm, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankValue_Throws(string value)
    {
        // Act
        var exception = Record.Exception(() => PostgresConnectionString.Normalize(value));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void Normalize_UriNamingNoDatabase_Throws()
    {
        // Arrange
        var uriWithoutDatabase = $"postgresql://{NewPostgresIdentifier()}:{NewPostgresIdentifier()}@{NewHost()}";

        // Act
        var exception = Record.Exception(() => PostgresConnectionString.Normalize(uriWithoutDatabase));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    private static string NewUriWithoutQuery() =>
        $"postgresql://{NewPostgresIdentifier()}:{NewPostgresIdentifier()}@{NewHost()}:{NewPortNumber().ToString(CultureInfo.InvariantCulture)}/{NewPostgresIdentifier()}";
}
