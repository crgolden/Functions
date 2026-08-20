namespace Functions.Curator.Enrichment;

using System.Globalization;

public static class ReleaseYear
{
    public static int? FromDate(DateOnly? released) => released?.Year;

    public static int? FromText(string? released)
    {
        if (string.IsNullOrWhiteSpace(released))
        {
            return null;
        }

        var prefix = released.Length >= 4 ? released[..4] : released;
        return prefix.Length == 4 && prefix.All(char.IsAsciiDigit)
            ? int.Parse(prefix, CultureInfo.InvariantCulture)
            : null;
    }
}
