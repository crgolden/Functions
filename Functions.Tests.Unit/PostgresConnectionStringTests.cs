namespace Functions.Tests.Unit;

using System.Globalization;
using Npgsql;
using static PostgresConnectionStringFixtureConstants;
using static TestSupport.TestValues;

[Trait("Category", "Unit")]
public sealed class PostgresConnectionStringTests
{
    public static TheoryData<string> UriSchemes() => [PostgresConnectionString.UriScheme, PostgresConnectionString.ShortUriScheme];

    public static TheoryData<string, SslMode> LibpqSslModeSpellings() => new()
    {
        { LibpqDisable, SslMode.Disable },
        { LibpqAllow, SslMode.Allow },
        { LibpqPrefer, SslMode.Prefer },
        { LibpqRequire, SslMode.Require },
        { LibpqVerifyCa, SslMode.VerifyCA },
        { LibpqVerifyFull, SslMode.VerifyFull },
        { LibpqVerifyFull.ToUpperInvariant(), SslMode.VerifyFull },
    };

    public static TheoryData<string> BlankValues() => [string.Empty, NewBlankRun()];

    [Fact]
    public void UriSchemes_AreTheTwoSpellingsLibpqAccepts()
    {
        // Assert
        Assert.Equal("postgresql", PostgresConnectionString.UriScheme);
        Assert.Equal("postgres", PostgresConnectionString.ShortUriScheme);
    }

    [Theory]
    [MemberData(nameof(UriSchemes))]
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
        var databaseUri = $"{PostgresConnectionString.UriScheme}://{NewPostgresIdentifier()}:{NewPostgresIdentifier()}@{NewHost()}/{NewPostgresIdentifier()}";

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
        var encodedPassword = Uri.EscapeDataString(decodedPassword);
        var databaseUri = $"{PostgresConnectionString.UriScheme}://{NewPostgresIdentifier()}:{encodedPassword}@{NewHost()}/{NewPostgresIdentifier()}";

        // Act
        var normalized = PostgresConnectionString.Normalize(databaseUri);

        // Assert
        Assert.Equal(decodedPassword, new NpgsqlConnectionStringBuilder(normalized).Password);
    }

    [Theory]
    [MemberData(nameof(LibpqSslModeSpellings))]
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
            $"{NewUriWithoutQuery()}?{PostgresConnectionString.SslModeParameter}={LibpqVerifyFull}&{LibpqSslRootCertParameter}={LibpqSystemRootCert}";

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
        var keywordForm = new NpgsqlConnectionStringBuilder
        {
            Host = NewHost(),
            Port = NewPortNumber(),
            Database = NewPostgresIdentifier(),
            Username = NewPostgresIdentifier(),
            Password = NewPostgresIdentifier(),
        }.ConnectionString;

        // Act
        var normalized = PostgresConnectionString.Normalize(keywordForm);

        // Assert
        Assert.Equal(keywordForm, normalized);
    }

    [Theory]
    [MemberData(nameof(BlankValues))]
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
        var uriWithoutDatabase = $"{PostgresConnectionString.UriScheme}://{NewPostgresIdentifier()}:{NewPostgresIdentifier()}@{NewHost()}";

        // Act
        var exception = Record.Exception(() => PostgresConnectionString.Normalize(uriWithoutDatabase));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    private static string NewUriWithoutQuery() =>
        $"{PostgresConnectionString.UriScheme}://{NewPostgresIdentifier()}:{NewPostgresIdentifier()}@{NewHost()}:{NewPortNumber().ToString(CultureInfo.InvariantCulture)}/{NewPostgresIdentifier()}";
}
