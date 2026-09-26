namespace Functions;

using Npgsql;

public static class PostgresConnectionString
{
    internal const string SslModeParameter = "sslmode";
    internal const string UriScheme = "postgresql";
    internal const string ShortUriScheme = "postgres";

    private static readonly Dictionary<string, SslMode> SslModes = new(StringComparer.OrdinalIgnoreCase)
    {
        [LibpqSslModes.Disable] = SslMode.Disable,
        [LibpqSslModes.Allow] = SslMode.Allow,
        [LibpqSslModes.Prefer] = SslMode.Prefer,
        [LibpqSslModes.Require] = SslMode.Require,
        [LibpqSslModes.VerifyCa] = SslMode.VerifyCA,
        [LibpqSslModes.VerifyFull] = SslMode.VerifyFull,
    };

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(value));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(string.Equals(uri.Scheme, UriScheme, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, ShortUriScheme, StringComparison.OrdinalIgnoreCase)))
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

        var requestedSslMode = FindSslMode(uri.Query);
        if (requestedSslMode is not null)
        {
            if (!SslModes.TryGetValue(requestedSslMode, out var sslMode))
            {
                throw new ArgumentException($"Unknown PostgreSQL '{SslModeParameter}' value '{requestedSslMode}'.", nameof(value));
            }

            builder.SslMode = sslMode;
        }

        return builder.ConnectionString;
    }

    private static string? FindSslMode(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var valueStart = separator + 1;
            if (string.Equals(pair[..separator], SslModeParameter, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[valueStart..]);
            }
        }

        return null;
    }

    internal static class LibpqSslModes
    {
        internal const string Disable = "disable";
        internal const string Allow = "allow";
        internal const string Prefer = "prefer";
        internal const string Require = "require";
        internal const string VerifyCa = "verify-ca";
        internal const string VerifyFull = "verify-full";
    }
}
