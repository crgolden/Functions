namespace Functions;

using System.Text.Json;
using System.Text.RegularExpressions;

public static partial class Normalizer
{
    public static string? NormalizeBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

        return digits.Length == 10 ? $"+1{digits}" : null;
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
            return trimmed.ToUpperInvariant();
        }

        var mapped = FullStateNameToCode(trimmed);
        if (mapped is not null)
        {
            return mapped;
        }

        var letters = new string(trimmed.Where(char.IsLetter).ToArray());
        return letters.Length == 2 ? letters.ToUpperInvariant() : null;
    }

    private static string? FullStateNameToCode(string state) => state.Trim().ToLowerInvariant() switch
    {
        "alabama" => "AL",
        "alaska" => "AK",
        "arizona" => "AZ",
        "arkansas" => "AR",
        "california" => "CA",
        "colorado" => "CO",
        "connecticut" => "CT",
        "delaware" => "DE",
        "district of columbia" or "washington, d.c." or "washington dc" => "DC",
        "florida" => "FL",
        "georgia" => "GA",
        "hawaii" => "HI",
        "idaho" => "ID",
        "illinois" => "IL",
        "indiana" => "IN",
        "iowa" => "IA",
        "kansas" => "KS",
        "kentucky" => "KY",
        "louisiana" => "LA",
        "maine" => "ME",
        "maryland" => "MD",
        "massachusetts" => "MA",
        "michigan" => "MI",
        "minnesota" => "MN",
        "mississippi" => "MS",
        "missouri" => "MO",
        "montana" => "MT",
        "nebraska" => "NE",
        "nevada" => "NV",
        "new hampshire" => "NH",
        "new jersey" => "NJ",
        "new mexico" => "NM",
        "new york" => "NY",
        "north carolina" => "NC",
        "north dakota" => "ND",
        "ohio" => "OH",
        "oklahoma" => "OK",
        "oregon" => "OR",
        "pennsylvania" => "PA",
        "rhode island" => "RI",
        "south carolina" => "SC",
        "south dakota" => "SD",
        "tennessee" => "TN",
        "texas" => "TX",
        "utah" => "UT",
        "vermont" => "VT",
        "virginia" => "VA",
        "washington" => "WA",
        "west virginia" or "w. va." or "w.va." => "WV",
        "wisconsin" => "WI",
        "wyoming" => "WY",
        _ => null,
    };

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();
}