namespace Functions;

using System.Text.RegularExpressions;

public static partial class Normalizer
{
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = DigitsOnly().Replace(phone, string.Empty);

        // Strip leading +1 country code if present (international prefix)
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

        url = url.Trim().TrimEnd('/');

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        // Upgrade http to https
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

        // The common case: the source already carries a 2-letter code.
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && char.IsLetter(trimmed[1]))
        {
            return trimmed.ToUpperInvariant();
        }

        // [dbo].[Churches].State is NCHAR(2), and ChurchBuilder/CampusBuilder's domain validation
        // requires exactly 2 letters — OSM addr:state and OpenAI-enriched state fields are both
        // inconsistent, sometimes carrying a full state name ("Ohio", "Alaska") or a hand-typed
        // abbreviation ("W. Va."). An unrecognized full name previously reached ChurchWriter
        // unnormalized and threw a permanent ArgumentException on every Service Bus delivery
        // attempt, guaranteeing MaxDeliveryCountExceeded regardless of retries. Map the known full
        // names, then fall back to stripping punctuation ("-IL" -> "IL"); anything still
        // unrecognized yields null so the caller can decide how to handle a missing state.
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
