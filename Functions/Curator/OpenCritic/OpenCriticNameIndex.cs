namespace Functions.Curator.OpenCritic;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public sealed class OpenCriticNameIndex
{
    private const int MinSubstringSearchKeyLength = 6;
    private const int MinCatalogPrefixKeyLength = 8;

    private static readonly (string Pattern, string Replacement)[] RomanToArabic =
    [
        (@"\bviii\b", "8"),
        (@"\bvii\b", "7"),
        (@"\bvi\b", "6"),
        (@"\bix\b", "9"),
        (@"\biv\b", "4"),
        (@"\biii\b", "3"),
        (@"\bii\b", "2"),
    ];

    private static readonly string[] SubtitleSeparators = [": ", " - "];

    private static readonly Regex TrademarkSigns = new("[™®©]", RegexOptions.None, RegexTimeout);
    private static readonly Regex ParenthesisedMarks = new(@"\(tm\)|\(r\)|\(c\)", RegexOptions.None, RegexTimeout);
    private static readonly Regex Quotes = new("['’‘ʼ`\"]+", RegexOptions.None, RegexTimeout);
    private static readonly Regex Dashes = new("[–—-]+", RegexOptions.None, RegexTimeout);
    private static readonly Regex NonAlphanumeric = new("[^a-z0-9 ]+", RegexOptions.None, RegexTimeout);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, RegexTimeout);
    private static readonly Regex TrailingYear = new(@"\s*\(\d{4}\)\s*$", RegexOptions.None, RegexTimeout);

    private readonly Dictionary<string, List<OpenCriticGame>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<OpenCriticGame>> _byNameWithoutSpaces = new(StringComparer.Ordinal);
    private readonly List<string> _insertionOrderedNames = [];

    private static TimeSpan RegexTimeout => TimeSpan.FromSeconds(5);

    public static OpenCriticNameIndex Build(IEnumerable<OpenCriticGame> games)
    {
        var index = new OpenCriticNameIndex();
        foreach (var game in games)
        {
            var key = Normalize(game.Name);
            index.Add(key, game);

            var shortKey = Normalize(StripSubtitle(game.Name));
            if (!string.Equals(shortKey, key, StringComparison.Ordinal))
            {
                index.Add(shortKey, game);
            }

            var yearStripped = TrailingYear.Replace(game.Name, string.Empty).Trim();
            if (string.Equals(yearStripped, game.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var yearStrippedKey = Normalize(yearStripped);
            if (!IsAnyOf(yearStrippedKey, key, shortKey))
            {
                index.Add(yearStrippedKey, game);
            }

            var yearStrippedShortKey = Normalize(StripSubtitle(yearStripped));
            if (!IsAnyOf(yearStrippedShortKey, key, shortKey, yearStrippedKey))
            {
                index.Add(yearStrippedShortKey, game);
            }
        }

        return index;
    }

    public static string Normalize(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormKD);
        var withoutCombining = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                withoutCombining.Append(character);
            }
        }

        normalized = withoutCombining.ToString().ToLowerInvariant();
        normalized = TrademarkSigns.Replace(normalized, string.Empty);
        normalized = ParenthesisedMarks.Replace(normalized, string.Empty);
        normalized = Quotes.Replace(normalized, string.Empty);
        normalized = Dashes.Replace(normalized, " ");
        normalized = NonAlphanumeric.Replace(normalized, " ");
        normalized = Whitespace.Replace(normalized, " ").Trim();
        foreach (var (pattern, replacement) in RomanToArabic)
        {
            normalized = Regex.Replace(normalized, pattern, replacement, RegexOptions.None, RegexTimeout);
        }

        return normalized;
    }

    public static string StripSubtitle(string title)
    {
        foreach (var separator in SubtitleSeparators)
        {
            var at = title.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 0)
            {
                return title[..at].Trim();
            }
        }

        return title;
    }

    public OpenCriticGame? FindMatch(string title)
    {
        var key = Normalize(title);
        if (_byName.TryGetValue(key, out var exact))
        {
            return Best(exact);
        }

        var shortKey = Normalize(StripSubtitle(title));
        if (!string.Equals(shortKey, key, StringComparison.Ordinal) && _byName.TryGetValue(shortKey, out var subtitleStripped))
        {
            return Best(subtitleStripped);
        }

        var nospaceKey = key.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (_byNameWithoutSpaces.TryGetValue(nospaceKey, out var spaceStripped))
        {
            return Best(spaceStripped);
        }

        var shortNospaceKey = shortKey.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (!string.Equals(shortNospaceKey, nospaceKey, StringComparison.Ordinal)
            && _byNameWithoutSpaces.TryGetValue(shortNospaceKey, out var subtitleAndSpaceStripped))
        {
            return Best(subtitleAndSpaceStripped);
        }

        var searchKey = shortKey.Length >= key.Length - 2 ? shortKey : key;
        if (searchKey.Length >= MinSubstringSearchKeyLength)
        {
            var pattern = $@"\b{Regex.Escape(searchKey)}\b";
            var candidates = _insertionOrderedNames
                .Where(name => Regex.IsMatch(name, pattern, RegexOptions.None, RegexTimeout))
                .SelectMany(name => _byName[name])
                .ToList();

            if (candidates.Count > 0)
            {
                return Best(candidates);
            }
        }

        OpenCriticGame? bestMatch = null;
        var bestLength = 0;
        foreach (var name in _insertionOrderedNames)
        {
            if (name.Length < MinCatalogPrefixKeyLength
                || !Regex.IsMatch(key, $@"^{Regex.Escape(name)}\b", RegexOptions.None, RegexTimeout)
                || name.Length <= bestLength)
            {
                continue;
            }

            bestMatch = Best(_byName[name]);
            bestLength = name.Length;
        }

        return bestMatch;
    }

    private static bool IsAnyOf(string candidate, params string[] others) =>
        Array.Exists(others, other => string.Equals(candidate, other, StringComparison.Ordinal));

    private static OpenCriticGame Best(List<OpenCriticGame> candidates)
    {
        OpenCriticGame? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.TopCriticScore is not { } score)
            {
                continue;
            }

            if (best is null || score > (best.TopCriticScore ?? 0.0))
            {
                best = candidate;
            }
        }

        return best ?? candidates[0];
    }

    private void Add(string key, OpenCriticGame game)
    {
        if (!_byName.TryGetValue(key, out var forName))
        {
            forName = [];
            _byName[key] = forName;
            _insertionOrderedNames.Add(key);
        }

        forName.Add(game);

        var nospaceKey = key.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (!_byNameWithoutSpaces.TryGetValue(nospaceKey, out var forNospaceName))
        {
            forNospaceName = [];
            _byNameWithoutSpaces[nospaceKey] = forNospaceName;
        }

        forNospaceName.Add(game);
    }
}
