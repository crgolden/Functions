namespace Functions;

using System.Data;
using System.Data.Common;
using System.Linq;
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

        var churches = await LoadCandidateChurchesAsync(cancellationToken);
        if (churches.Count == 0)
        {
            return;
        }

        var (latCellDegrees, lonCellDegrees) = ComputeCellSize(churches);
        var buckets = BuildBuckets(churches, latCellDegrees, lonCellDegrees);
        await FindAndWriteMatchesAsync(churches, buckets, cancellationToken);
    }

    internal static (long LatBucket, long LonBucket) BucketKey(double lat, double lng, double latCellDegrees, double lonCellDegrees) =>
        ((long)Math.Floor(lat / latCellDegrees), (long)Math.Floor(lng / lonCellDegrees));

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

        var (s1Matches, s2Matches, matches) = FindMatches(s1, s2);
        if (matches == 0)
        {
            return 0.0;
        }

        var transpositions = CountTranspositions(s1, s2, s1Matches, s2Matches);
        var jaro = ComputeJaroScore(s1.Length, s2.Length, matches, transpositions);
        var prefix = CommonPrefixLength(s1, s2);
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

    private static (bool[] S1Matches, bool[] S2Matches, int Matches) FindMatches(string s1, string s2)
    {
        var matchWindow = (Math.Max(s1.Length, s2.Length) / 2) - 1;
        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];
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

        return (s1Matches, s2Matches, matches);
    }

    private static int CountTranspositions(string s1, string s2, bool[] s1Matches, bool[] s2Matches)
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
        for (var i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
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
        // Bucket churches into a lat/lon grid sized to MaxDistanceMiles so only same-or-adjacent
        // cells need pairwise comparison, instead of an O(n^2) scan over every active church.
        // Longitude degrees shrink in real-world width as latitude increases (milesPerDegree =
        // ~69.1 * cos(lat)), so the longitude cell size is derived from the dataset's highest
        // absolute latitude (smallest cosine) to guarantee every cell is at least MaxDistanceMiles
        // wide in every direction present in the data — never narrower, so no pair within
        // MaxDistanceMiles can land outside the 3x3 neighbor-cell search below.
        const double milesPerDegreeLatitude = 69.1;
        var latCellDegrees = MaxDistanceMiles / milesPerDegreeLatitude;
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

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static bool IsLikelyDuplicate(
        (Guid Id, string Name, double Lat, double Lng) a, (Guid Id, string Name, double Lat, double Lng) b)
    {
        var distance = HaversineDistance(a.Lat, a.Lng, b.Lat, b.Lng);
        if (distance > MaxDistanceMiles)
        {
            return false;
        }

        var similarity = JaroWinkler(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant());
        return similarity >= JaroWinklerThreshold;
    }

    private async Task<List<(Guid Id, string Name, double Lat, double Lng)>> LoadCandidateChurchesAsync(CancellationToken ct)
    {
        await using var cmd = _dbConnection.CreateCommand();

        // PO Box addresses have no precise street-level geocode — Census resolves them to a
        // city/ZIP centroid, so unrelated churches that both filed with a PO Box in the same
        // area land within MaxDistanceMiles of each other by coincidence, producing false-positive
        // merge suggestions with no real relationship to name similarity.
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
            churches.Add(((Guid)reader[0], (string)reader[1], (double)reader[2], (double)reader[3]));
        }

        await reader.CloseAsync();
        return churches;
    }

    private async Task FindAndWriteMatchesAsync(
        List<(Guid Id, string Name, double Lat, double Lng)> churches,
        Dictionary<(long LatBucket, long LonBucket), List<int>> buckets,
        CancellationToken ct)
    {
        var processedPairs = new HashSet<(int, int)>();
        foreach (var (key, indices) in buckets)
        {
            for (var dLat = -1; dLat <= 1; dLat++)
            {
                for (var dLon = -1; dLon <= 1; dLon++)
                {
                    if (!buckets.TryGetValue((key.LatBucket + dLat, key.LonBucket + dLon), out var neighborIndices))
                    {
                        continue;
                    }

                    await MatchNeighborCellAsync(churches, indices, neighborIndices, processedPairs, ct);
                }
            }
        }
    }

    private async Task MatchNeighborCellAsync(
        List<(Guid Id, string Name, double Lat, double Lng)> churches,
        List<int> indices,
        List<int> neighborIndices,
        HashSet<(int, int)> processedPairs,
        CancellationToken ct)
    {
        foreach (var i in indices)
        {
            foreach (var j in neighborIndices)
            {
                if (i >= j || !processedPairs.Add((i, j)))
                {
                    continue;
                }

                var a = churches[i];
                var b = churches[j];
                if (!IsLikelyDuplicate(a, b))
                {
                    continue;
                }

                await WriteSuggestionAsync(a.Id, b.Id, ct);
            }
        }
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
