namespace Functions.Tests.Unit;

using System.Globalization;
using Npgsql;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PostgresConnectionStringTests
{
    private const int DefaultPostgresPort = 5432;

    [Theory]
    [InlineData("postgresql")]
    [InlineData("postgres")]
    public void Normalize_UriForm_ProducesConnectionStringNpgsqlCanParse(string scheme)
    {
        // Arrange
        var databaseHost = NewHost();
        var databasePort = NewPort();
        var databaseName = NewIdentifier();
        var databaseUser = NewIdentifier();
        var databasePassword = NewIdentifier();

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
        var databaseUri = $"postgresql://{NewIdentifier()}:{NewIdentifier()}@{NewHost()}/{NewIdentifier()}";

        // Act
        var normalized = PostgresConnectionString.Normalize(databaseUri);

        // Assert
        Assert.Equal(DefaultPostgresPort, new NpgsqlConnectionStringBuilder(normalized).Port);
    }

    [Fact]
    public void Normalize_PercentEncodedPassword_IsDecoded()
    {
        // Arrange
        var decodedPassword = $"{NewIdentifier()}@{NewIdentifier()}:{NewIdentifier()}";
        var encodedPassword = decodedPassword.Replace("@", "%40", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal);
        var databaseUri = $"postgresql://{NewIdentifier()}:{encodedPassword}@{NewHost()}/{NewIdentifier()}";

        // Act
        var normalized = PostgresConnectionString.Normalize(databaseUri);

        // Assert
        Assert.Equal(decodedPassword, new NpgsqlConnectionStringBuilder(normalized).Password);
    }

    [Fact]
    public void Normalize_KeywordForm_IsReturnedUnchanged()
    {
        // Arrange
        var keywordForm =
            $"Host={NewHost()};Port={NewPort().ToString(CultureInfo.InvariantCulture)};Database={NewIdentifier()};Username={NewIdentifier()};Password={NewIdentifier()}";

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
        var uriWithoutDatabase = $"postgresql://{NewIdentifier()}:{NewIdentifier()}@{NewHost()}";

        // Act
        var exception = Record.Exception(() => PostgresConnectionString.Normalize(uriWithoutDatabase));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    private static string NewHost() => TestValues.NewHost();

    private static string NewIdentifier() => $"id{Guid.NewGuid():N}";

    private static int NewPort() => Random.Shared.Next(1024, 65535);
}
