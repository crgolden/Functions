namespace Functions.Churches;

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shared.Domain;

public static partial class Normalizer
{
    internal const string NorthAmericanE164Prefix = "+1";

    internal static readonly FrozenDictionary<string, string> StateCodesByFullName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alabama"] = "AL",
            ["alaska"] = "AK",
            ["arizona"] = "AZ",
            ["arkansas"] = "AR",
            ["california"] = "CA",
            ["colorado"] = "CO",
            ["connecticut"] = "CT",
            ["delaware"] = "DE",
            ["district of columbia"] = "DC",
            ["washington, d.c."] = "DC",
            ["washington dc"] = "DC",
            ["florida"] = "FL",
            ["georgia"] = "GA",
            ["hawaii"] = "HI",
            ["idaho"] = "ID",
            ["illinois"] = "IL",
            ["indiana"] = "IN",
            ["iowa"] = "IA",
            ["kansas"] = "KS",
            ["kentucky"] = "KY",
            ["louisiana"] = "LA",
            ["maine"] = "ME",
            ["maryland"] = "MD",
            ["massachusetts"] = "MA",
            ["michigan"] = "MI",
            ["minnesota"] = "MN",
            ["mississippi"] = "MS",
            ["missouri"] = "MO",
            ["montana"] = "MT",
            ["nebraska"] = "NE",
            ["nevada"] = "NV",
            ["new hampshire"] = "NH",
            ["new jersey"] = "NJ",
            ["new mexico"] = "NM",
            ["new york"] = "NY",
            ["north carolina"] = "NC",
            ["north dakota"] = "ND",
            ["ohio"] = "OH",
            ["oklahoma"] = "OK",
            ["oregon"] = "OR",
            ["pennsylvania"] = "PA",
            ["rhode island"] = "RI",
            ["south carolina"] = "SC",
            ["south dakota"] = "SD",
            ["tennessee"] = "TN",
            ["texas"] = "TX",
            ["utah"] = "UT",
            ["vermont"] = "VT",
            ["virginia"] = "VA",
            ["washington"] = "WA",
            ["west virginia"] = "WV",
            ["w. va."] = "WV",
            ["w.va."] = "WV",
            ["wisconsin"] = "WI",
            ["wyoming"] = "WY",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string? NormalizeBlank(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var visible = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            var category = char.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.Format)
            {
                continue;
            }

            visible.Append(category == UnicodeCategory.SpaceSeparator ? ' ' : ch);
        }

        var collapsed = RunsOfSpaces().Replace(visible.ToString(), " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    public static string? GetJsonString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? NormalizeBlank(value.GetString())
            : null;

    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = DigitsOnly().Replace(phone, string.Empty);

        if (digits.Length == 11 && digits[0] == '1')
        {
            digits = digits[1..];
        }

        return digits.Length == 10 ? $"{NorthAmericanE164Prefix}{digits}" : null;
    }

    public static string? NormalizeZip(string? zip)
    {
        if (string.IsNullOrWhiteSpace(zip))
        {
            return null;
        }

        var digits = DigitsOnly().Replace(zip, string.Empty);
        return digits.Length >= 5 ? digits[..5] : null;
    }

    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Split(';', 2)[0];

        url = url.Trim().TrimEnd('/');

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url[7..];
        }

        return url;
    }

    public static string? NormalizeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var trimmed = state.Trim();

        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && char.IsLetter(trimmed[1]))
        {
            return InUspsSet(trimmed.ToUpperInvariant());
        }

        var mapped = FullStateNameToCode(trimmed);
        if (mapped is not null)
        {
            return mapped;
        }

        var letters = new string(trimmed.Where(char.IsLetter).ToArray());
        return letters.Length == 2 ? InUspsSet(letters.ToUpperInvariant()) : null;
    }

    private static string? InUspsSet(string code) => StateCodes.TryParse(code, out _) ? code : null;

    private static string? FullStateNameToCode(string state) =>
        StateCodesByFullName.GetValueOrDefault(state.Trim().ToLowerInvariant());

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex(" {2,}")]
    private static partial Regex RunsOfSpaces();
}
