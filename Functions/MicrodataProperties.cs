namespace Functions;

public static class MicrodataProperties
{
    public const string Name = "name";

    public const string StreetAddress = "streetAddress";

    public const string AddressLocality = "addressLocality";

    public const string AddressRegion = "addressRegion";

    public const string PostalCode = "postalCode";

    public const string Telephone = "telephone";

    public const string Email = "email";

    public static string Selector(string property) => $"[itemprop='{property}']";
}
