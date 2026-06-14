namespace Functions;

using System.Data;
using System.Data.Common;
using Microsoft.Azure.Functions.Worker;

public class DeduplicationJob
{
    private const double MaxDistanceMiles = 0.1;
    private const double JaroWinklerThreshold = 0.85;

    private readonly DbConnection _dbConnection;

    public DeduplicationJob(DbConnection dbConnection)
    {
        _dbConnection = dbConnection;
    }

    [Function(nameof(DeduplicationJob))]
    public async Task Run(
        [TimerTrigger("0 0 4 * * *")] TimerInfo timer,
        CancellationToken cancellationToken = default)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(cancellationToken);
        }

        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT [Id], [CanonicalName], [Latitude], [Longitude]
            FROM [dbo].[Churches]
            WHERE [IsActive] = 1
            ORDER BY [CreatedAt] DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var churches = new List<(Guid Id, string Name, double Lat, double Lng)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            churches.Add(((Guid)reader[0], (string)reader[1], (double)reader[2], (double)reader[3]));
        }

        await reader.CloseAsync();

        for (var i = 0; i < churches.Count; i++)
        {
            var a = churches[i];
            for (var j = i + 1; j < churches.Count; j++)
            {
                var b = churches[j];
                var distance = HaversineDistance(a.Lat, a.Lng, b.Lat, b.Lng);
                if (distance > MaxDistanceMiles)
                {
                    continue;
                }

                var similarity = JaroWinkler(
                    a.Name.ToLowerInvariant(),
                    b.Name.ToLowerInvariant());
                if (similarity < JaroWinklerThreshold)
                {
                    continue;
                }

                await WriteSuggestionAsync(a.Id, b.Id, cancellationToken);
            }
        }
    }

    internal static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2)
        {
            return 1.0;
        }

        if (s1.Length == 0 || s2.Length == 0)
        {
            return 0.0;
        }

        var matchWindow = (Math.Max(s1.Length, s2.Length) / 2) - 1;
        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];
        var matches = 0;
        var transpositions = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            var start = Math.Max(0, i - matchWindow);
            var end = Math.Min(i + matchWindow + 1, s2.Length);
            for (var j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j])
                {
                    continue;
                }

                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0.0;
        }

        var k = 0;
        for (var i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i])
            {
                continue;
            }

            while (!s2Matches[k])
            {
                k++;
            }

            if (s1[i] != s2[k])
            {
                transpositions++;
            }

            k++;
        }

        var mD = (double)matches;
        var jaro = ((mD / s1.Length) + (mD / s2.Length) + ((mD - (transpositions / 2.0)) / mD)) / 3.0;
        var prefix = 0;
        for (var i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] != s2[i])
            {
                break;
            }

            prefix++;
        }

        return jaro + (prefix * 0.1 * (1 - jaro));
    }

    internal static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3958.8;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
                + (Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    internal static double ToRad(double deg) => deg * (Math.PI / 180.0);

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private async Task WriteSuggestionAsync(Guid churchAId, Guid churchBId, CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM [dbo].[UserCorrections]
                WHERE [ChurchId] = @ChurchA AND [Field] = 'merge' AND [NewValue] = @ChurchBStr AND [Status] = 0
            )
            INSERT INTO [dbo].[UserCorrections]
                ([Id], [ChurchId], [UserId], [Field], [NewValue], [Status], [CreatedAt])
            VALUES (@Id, @ChurchA, 'system', 'merge', @ChurchBStr, 0, @Now)
            """;
        AddParam(cmd, "@Id", Guid.CreateVersion7(DateTimeOffset.UtcNow));
        AddParam(cmd, "@ChurchA", churchAId);
        AddParam(cmd, "@ChurchBStr", churchBId.ToString());
        AddParam(cmd, "@Now", DateTimeOffset.UtcNow.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
