namespace Functions;

using Npgsql;

/// <summary>
/// Normalises a PostgreSQL connection string to the keyword form Npgsql requires.
/// </summary>
public static class PostgresConnectionString
{
    /// <summary>
    /// Converts a <c>postgresql://</c> URI to Npgsql keyword form, passing keyword form through unchanged.
    /// </summary>
    /// <remarks>
    /// Curator owns the Postgres credential and stores it as a URI, the form psycopg takes. Npgsql parses
    /// only <c>keyword=value</c> pairs, so pointing this app's setting at that same secret without
    /// conversion fails at connection time. Converting here keeps one credential in Key Vault rather than
    /// a second copy in a second format, which would rotate out of step with the first.
    /// </remarks>
    /// <param name="value">A <c>postgresql://</c>/<c>postgres://</c> URI, or an Npgsql keyword string.</param>
    /// <returns>An Npgsql-parseable connection string.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is blank, or is a URI naming no database.</exception>
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(value));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "postgresql" && uri.Scheme != "postgres"))
        {
            return value;
        }

        var database = uri.AbsolutePath.TrimStart('/');
        if (database.Length == 0)
        {
            throw new ArgumentException("A PostgreSQL URI must name a database.", nameof(value));
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(database),
        };

        if (userInfo[0].Length > 0)
        {
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
        }

        if (userInfo.Length == 2 && userInfo[1].Length > 0)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        return builder.ConnectionString;
    }
}
