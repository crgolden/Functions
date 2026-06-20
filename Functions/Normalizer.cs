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

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();
}
