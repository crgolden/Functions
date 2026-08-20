namespace Functions;

using Npgsql;

public static class PostgresConnectionString
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(value));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(string.Equals(uri.Scheme, "postgresql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "postgres", StringComparison.OrdinalIgnoreCase)))
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
