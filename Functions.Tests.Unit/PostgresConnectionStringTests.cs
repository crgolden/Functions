namespace Functions.Tests.Unit;

using Npgsql;

[Trait("Category", "Unit")]
public sealed class PostgresConnectionStringTests
{
    [Theory]
    [InlineData("postgresql://curator_app:s3cret@db.example.com:6543/curator")]
    [InlineData("postgres://curator_app:s3cret@db.example.com:6543/curator")]
    public void Normalize_UriForm_ProducesConnectionStringNpgsqlCanParse(string uri)
    {
        // Act
        var normalized = PostgresConnectionString.Normalize(uri);

        // Assert
        var parsed = new NpgsqlConnectionStringBuilder(normalized);
        Assert.Equal("db.example.com", parsed.Host);
        Assert.Equal(6543, parsed.Port);
        Assert.Equal("curator", parsed.Database);
        Assert.Equal("curator_app", parsed.Username);
        Assert.Equal("s3cret", parsed.Password);
    }

    [Fact]
    public void Normalize_UriWithoutPort_DefaultsToPostgresPort()
    {
        // Act
        var normalized = PostgresConnectionString.Normalize("postgresql://curator_app:s3cret@localhost/curator");

        // Assert
        Assert.Equal(5432, new NpgsqlConnectionStringBuilder(normalized).Port);
    }

    [Fact]
    public void Normalize_PercentEncodedPassword_IsDecoded()
    {
        // Act
        var normalized = PostgresConnectionString.Normalize("postgresql://user:p%40ss%3Aword@localhost/curator");

        // Assert
        Assert.Equal("p@ss:word", new NpgsqlConnectionStringBuilder(normalized).Password);
    }

    [Fact]
    public void Normalize_KeywordForm_IsReturnedUnchanged()
    {
        // Arrange
        const string keyword = "Host=localhost;Port=5432;Database=curator;Username=curator_app;Password=s3cret";

        // Act
        var normalized = PostgresConnectionString.Normalize(keyword);

        // Assert
        Assert.Equal(keyword, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankValue_Throws(string value)
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => PostgresConnectionString.Normalize(value));
    }

    [Fact]
    public void Normalize_UriNamingNoDatabase_Throws()
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => PostgresConnectionString.Normalize("postgresql://user:pw@localhost"));
    }
}
