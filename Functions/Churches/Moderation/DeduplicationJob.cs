namespace Functions.Churches.Moderation;

using System.Data;
using System.Data.Common;
using Extensions;
using Microsoft.Azure.Functions.Worker;

public class DeduplicationJob
{
    internal const double MaxDistanceMiles = 0.1;
    internal const double MilesPerDegreeLatitude = 69.1;
    internal const double WinklerPrefixScale = 0.1;
    internal const int WinklerMaxPrefixLength = 4;

    private const double JaroWinklerThreshold = 0.85;
    private const int MaxStackallocMatchFlags = 256;
    private const int MaxSuggestionsPerInsert = 500;

    private static readonly (int LatOffset, int LonOffset)[] ForwardNeighborCells = [(0, 1), (1, -1), (1, 0), (1, 1)];

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

        var churches = await LoadCandidateChurchesAsync(cancellationToken);
        if (churches.Count == 0)
        {
            return;
        }

        var (latCellDegrees, lonCellDegrees) = ComputeCellSize(churches);
        var buckets = BuildBuckets(churches, latCellDegrees, lonCellDegrees);
        var suggestions = FindLikelyDuplicates(churches, buckets);
        await WriteSuggestionsAsync(suggestions, cancellationToken);
    }

    internal static (long LatBucket, long LonBucket) BucketKey(double lat, double lng, double latCellDegrees, double lonCellDegrees) =>
        ((long)Math.Floor(lat / latCellDegrees), (long)Math.Floor(lng / lonCellDegrees));

    internal static double JaroWinkler(string s1, string s2)
    {
        if (string.Equals(s1, s2, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (s1.Length == 0 || s2.Length == 0)
        {
            return 0.0;
        }

        Span<bool> s1Matches = s1.Length <= MaxStackallocMatchFlags ? stackalloc bool[s1.Length] : new bool[s1.Length];
        Span<bool> s2Matches = s2.Length <= MaxStackallocMatchFlags ? stackalloc bool[s2.Length] : new bool[s2.Length];
        var matches = FindMatches(s1, s2, s1Matches, s2Matches);
        if (matches == 0)
        {
            return 0.0;
        }

        var transpositions = CountTranspositions(s1, s2, s1Matches, s2Matches);
        var jaro = ComputeJaroScore(s1.Length, s2.Length, matches, transpositions);
        var prefix = CommonPrefixLength(s1, s2);
        return jaro + (prefix * WinklerPrefixScale * (1 - jaro));
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

    private static int FindMatches(string s1, string s2, Span<bool> s1Matches, Span<bool> s2Matches)
    {
        var matchWindow = (Math.Max(s1.Length, s2.Length) / 2) - 1;
        var matches = 0;
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

        return matches;
    }

    private static int CountTranspositions(string s1, string s2, ReadOnlySpan<bool> s1Matches, ReadOnlySpan<bool> s2Matches)
    {
        var k = 0;
        var transpositions = 0;
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

        return transpositions;
    }

    private static double ComputeJaroScore(int s1Length, int s2Length, int matches, int transpositions)
    {
        var mD = (double)matches;
        return ((mD / s1Length) + (mD / s2Length) + ((mD - (transpositions / 2.0)) / mD)) / 3.0;
    }

    private static int CommonPrefixLength(string s1, string s2)
    {
        var prefix = 0;
        for (var i = 0; i < Math.Min(WinklerMaxPrefixLength, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] != s2[i])
            {
                break;
            }

            prefix++;
        }

        return prefix;
    }

    private static (double LatCellDegrees, double LonCellDegrees) ComputeCellSize(
        List<(Guid Id, string Name, double Lat, double Lng)> churches)
    {
        var latCellDegrees = MaxDistanceMiles / MilesPerDegreeLatitude;
        var cosFloor = Math.Max(0.01, churches.Min(c => Math.Cos(ToRad(Math.Abs(c.Lat)))));
        return (latCellDegrees, latCellDegrees / cosFloor);
    }

    private static Dictionary<(long LatBucket, long LonBucket), List<int>> BuildBuckets(
        List<(Guid Id, string Name, double Lat, double Lng)> churches, double latCellDegrees, double lonCellDegrees)
    {
        var buckets = new Dictionary<(long LatBucket, long LonBucket), List<int>>();
        for (var i = 0; i < churches.Count; i++)
        {
            var key = BucketKey(churches[i].Lat, churches[i].Lng, latCellDegrees, lonCellDegrees);
            if (!buckets.TryGetValue(key, out var indices))
            {
                indices = [];
                buckets[key] = indices;
            }

            indices.Add(i);
        }

        return buckets;
    }

    private static List<(Guid ChurchAId, Guid ChurchBId)> FindLikelyDuplicates(
        List<(Guid Id, string Name, double Lat, double Lng)> churches,
        Dictionary<(long LatBucket, long LonBucket), List<int>> buckets)
    {
        var suggestions = new List<(Guid ChurchAId, Guid ChurchBId)>();
        foreach (var (key, indices) in buckets)
        {
            for (var a = 0; a < indices.Count; a++)
            {
                for (var b = a + 1; b < indices.Count; b++)
                {
                    AddIfLikelyDuplicate(churches, indices[a], indices[b], suggestions);
                }
            }

            foreach (var (latOffset, lonOffset) in ForwardNeighborCells)
            {
                if (!buckets.TryGetValue((key.LatBucket + latOffset, key.LonBucket + lonOffset), out var neighborIndices))
                {
                    continue;
                }

                foreach (var i in indices)
                {
                    foreach (var j in neighborIndices)
                    {
                        AddIfLikelyDuplicate(churches, Math.Min(i, j), Math.Max(i, j), suggestions);
                    }
                }
            }
        }

        return suggestions;
    }

    private static void AddIfLikelyDuplicate(
        List<(Guid Id, string Name, double Lat, double Lng)> churches,
        int earlierIndex,
        int laterIndex,
        List<(Guid ChurchAId, Guid ChurchBId)> suggestions)
    {
        var a = churches[earlierIndex];
        var b = churches[laterIndex];
        if (HaversineDistance(a.Lat, a.Lng, b.Lat, b.Lng) > MaxDistanceMiles
            || JaroWinkler(a.Name, b.Name) < JaroWinklerThreshold)
        {
            return;
        }

        suggestions.Add((a.Id, b.Id));
    }

    private async Task<List<(Guid Id, string Name, double Lat, double Lng)>> LoadCandidateChurchesAsync(CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();
        cmd.CommandText = """
            SELECT [Id], [CanonicalName], [Latitude], [Longitude]
            FROM [dbo].[Churches]
            WHERE [IsActive] = 1 AND NOT ([Latitude] = 0 AND [Longitude] = 0)
              AND [Street] NOT LIKE 'PO BOX%' AND [Street] NOT LIKE 'P O BOX%'
              AND [Street] NOT LIKE 'P.O. BOX%' AND [Street] NOT LIKE 'P.O BOX%'
            ORDER BY [CreatedAt] DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var churches = new List<(Guid Id, string Name, double Lat, double Lng)>();
        while (await reader.ReadAsync(ct))
        {
            churches.Add(((Guid)reader[0], ((string)reader[1]).ToLowerInvariant(), (double)reader[2], (double)reader[3]));
        }

        await reader.CloseAsync();
        return churches;
    }

    private async Task WriteSuggestionsAsync(List<(Guid ChurchAId, Guid ChurchBId)> suggestions, CancellationToken ct)
    {
        foreach (var chunk in suggestions.Chunk(MaxSuggestionsPerInsert))
        {
            await using var cmd = _dbConnection.CreateCommand();
            cmd.AddParam("@Now", DateTimeOffset.UtcNow);
            var rows = new List<string>(chunk.Length);
            for (var row = 0; row < chunk.Length; row++)
            {
                cmd.AddParam($"@Id{row}", Guid.CreateVersion7(DateTimeOffset.UtcNow));
                cmd.AddParam($"@ChurchA{row}", chunk[row].ChurchAId);
                cmd.AddParam($"@ChurchB{row}", chunk[row].ChurchBId.ToString());
                rows.Add($"(@Id{row}, @ChurchA{row}, @ChurchB{row})");
            }

            cmd.CommandText = $"""
                INSERT INTO [dbo].[UserCorrections]
                    ([Id], [ChurchId], [UserId], [Field], [NewValue], [Status], [CreatedAt])
                SELECT [Suggested].[Id], [Suggested].[ChurchId], 'system', 'merge', [Suggested].[NewValue], 0, @Now
                FROM (VALUES {string.Join(", ", rows)}) AS [Suggested] ([Id], [ChurchId], [NewValue])
                WHERE NOT EXISTS (
                    SELECT 1 FROM [dbo].[UserCorrections] AS [Existing]
                    WHERE [Existing].[ChurchId] = [Suggested].[ChurchId] AND [Existing].[Field] = 'merge'
                      AND [Existing].[NewValue] = [Suggested].[NewValue] AND [Existing].[Status] = 0
                )
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}