namespace Functions.Curator.Enrichment;

public static class EnrichmentProviderNames
{
    public const string Rawg = "rawg";

    public const string OpenCritic = "opencritic";

    public const string Psn = "psn";

    public static string ToWireName(this EnrichmentProvider provider) => provider switch
    {
        EnrichmentProvider.Rawg => Rawg,
        EnrichmentProvider.OpenCritic => OpenCritic,
        EnrichmentProvider.Psn => Psn,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static EnrichmentProvider? FromWireName(string? wireName) => wireName switch
    {
        Rawg => EnrichmentProvider.Rawg,
        OpenCritic => EnrichmentProvider.OpenCritic,
        Psn => EnrichmentProvider.Psn,
        _ => null,
    };

    public static List<string> ToWireNames(this IEnumerable<EnrichmentProvider> providers) =>
        providers.Select(ToWireName).ToList();
}
