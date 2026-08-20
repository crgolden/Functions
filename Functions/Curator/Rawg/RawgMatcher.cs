namespace Functions.Curator.Rawg;

using System.Text.RegularExpressions;

public static class RawgMatcher
{
    public const int Ps4PlatformId = 18;
    public const int Ps5PlatformId = 187;
    public const double DefaultMatchThreshold = 0.45;

    private static readonly Regex TrademarkSigns = new("[™®©]", RegexOptions.None, RegexTimeout);
    private static readonly Regex Quotes = new("['’‘ʼ`]", RegexOptions.None, RegexTimeout);
    private static readonly Regex Dashes = new("[–—]", RegexOptions.None, RegexTimeout);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, RegexTimeout);

    private static TimeSpan RegexTimeout => TimeSpan.FromSeconds(5);

    public static string Normalize(string title)
    {
        var normalized = TrademarkSigns.Replace(title, string.Empty);
        normalized = Quotes.Replace(normalized, "'");
        normalized = Dashes.Replace(normalized, "-");
        normalized = Whitespace.Replace(normalized, " ");
        return normalized.Trim().ToLowerInvariant();
    }

    public static double Similarity(string a, string b)
    {
        var normalizedA = Normalize(a);
        var normalizedB = Normalize(b);
        var total = normalizedA.Length + normalizedB.Length;
        if (total == 0)
        {
            return 1.0;
        }

        var matches = MatchedCharacterCount(normalizedA, normalizedB);
        return 2.0 * matches / total;
    }

    public static RawgCandidate? FindBestMatch(
        string title,
        IReadOnlyList<RawgCandidate> candidates,
        double threshold = DefaultMatchThreshold)
    {
        RawgCandidate? best = null;
        var bestScore = 0.0;
        foreach (var candidate in candidates)
        {
            if (!candidate.PlatformIds.Contains(Ps4PlatformId) && !candidate.PlatformIds.Contains(Ps5PlatformId))
            {
                continue;
            }

            var score = Similarity(title, candidate.Name);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best is null || bestScore < threshold ? null : best;
    }

    private static int MatchedCharacterCount(string a, string b)
    {
        var b2j = BuildIndex(b);
        var blocks = new List<int>();
        var pending = new Stack<(int ALo, int AHi, int BLo, int BHi)>();
        pending.Push((0, a.Length, 0, b.Length));
        while (pending.Count > 0)
        {
            var (aLo, aHi, bLo, bHi) = pending.Pop();
            var (i, j, k) = FindLongestMatch(a, b, b2j, aLo, aHi, bLo, bHi);
            if (k <= 0)
            {
                continue;
            }

            blocks.Add(k);
            if (aLo < i && bLo < j)
            {
                pending.Push((aLo, i, bLo, j));
            }

            if (i + k < aHi && j + k < bHi)
            {
                pending.Push((i + k, aHi, j + k, bHi));
            }
        }

        return blocks.Sum();
    }

    private static Dictionary<char, List<int>> BuildIndex(string b)
    {
        var b2j = new Dictionary<char, List<int>>();
        for (var i = 0; i < b.Length; i++)
        {
            if (!b2j.TryGetValue(b[i], out var indices))
            {
                indices = [];
                b2j[b[i]] = indices;
            }

            indices.Add(i);
        }

        if (b.Length < 200)
        {
            return b2j;
        }

        var threshold = (b.Length / 100) + 1;
        foreach (var popular in b2j.Where(pair => pair.Value.Count > threshold).Select(pair => pair.Key).ToList())
        {
            b2j.Remove(popular);
        }

        return b2j;
    }

    private static (int I, int J, int K) FindLongestMatch(
        string a,
        string b,
        Dictionary<char, List<int>> b2j,
        int aLo,
        int aHi,
        int bLo,
        int bHi)
    {
        var bestI = aLo;
        var bestJ = bLo;
        var bestSize = 0;
        var j2Len = new Dictionary<int, int>();
        for (var i = aLo; i < aHi; i++)
        {
            var newJ2Len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var indices))
            {
                foreach (var j in indices)
                {
                    if (j < bLo)
                    {
                        continue;
                    }

                    if (j >= bHi)
                    {
                        break;
                    }

                    var k = j2Len.GetValueOrDefault(j - 1) + 1;
                    newJ2Len[j] = k;
                    if (k > bestSize)
                    {
                        bestI = i - k + 1;
                        bestJ = j - k + 1;
                        bestSize = k;
                    }
                }
            }

            j2Len = newJ2Len;
        }

        while (bestI > aLo && bestJ > bLo && a[bestI - 1] == b[bestJ - 1])
        {
            bestI--;
            bestJ--;
            bestSize++;
        }

        while (bestI + bestSize < aHi && bestJ + bestSize < bHi && a[bestI + bestSize] == b[bestJ + bestSize])
        {
            bestSize++;
        }

        return (bestI, bestJ, bestSize);
    }
}
