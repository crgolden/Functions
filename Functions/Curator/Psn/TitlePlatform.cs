namespace Functions.Curator.Psn;

public static class TitlePlatform
{
    private static readonly Dictionary<string, string> PlatformByTitleIdPrefix = new(StringComparer.Ordinal)
    {
        ["PPSA"] = "PS5",
        ["CUSA"] = "PS4",
        ["BLUS"] = "PS3",
        ["BLES"] = "PS3",
        ["BLJM"] = "PS3",
        ["BLJS"] = "PS3",
        ["BCUS"] = "PS3",
        ["BCES"] = "PS3",
        ["BCJS"] = "PS3",
        ["BCAS"] = "PS3",
        ["NPUB"] = "PS3",
        ["NPEB"] = "PS3",
        ["NPJB"] = "PS3",
        ["NPHB"] = "PS3",
        ["NPUA"] = "PS3",
        ["NPEA"] = "PS3",
        ["NPJA"] = "PS3",
        ["NPHA"] = "PS3",
        ["NPUO"] = "PS3",
        ["NPEO"] = "PS3",
        ["NPUX"] = "PS3",
        ["NPEX"] = "PS3",
        ["PCSA"] = "PSVITA",
        ["PCSB"] = "PSVITA",
        ["PCSC"] = "PSVITA",
        ["PCSD"] = "PSVITA",
        ["PCSE"] = "PSVITA",
        ["PCSF"] = "PSVITA",
        ["PCSG"] = "PSVITA",
        ["PCSH"] = "PSVITA",
        ["VLUS"] = "PSVITA",
        ["VCUS"] = "PSVITA",
        ["VCJS"] = "PSVITA",
        ["VCAS"] = "PSVITA",
        ["NPVA"] = "PSVITA",
        ["NPVB"] = "PSVITA",
        ["NPVC"] = "PSVITA",
        ["NPVX"] = "PSVITA",
        ["UCUS"] = "PSP",
        ["UCES"] = "PSP",
        ["UCJS"] = "PSP",
        ["UCAS"] = "PSP",
        ["ULUS"] = "PSP",
        ["ULES"] = "PSP",
        ["ULJM"] = "PSP",
        ["ULJS"] = "PSP",
        ["NPUG"] = "PSP",
        ["NPEG"] = "PSP",
        ["NPJG"] = "PSP",
        ["NPHG"] = "PSP",
        ["NPUZ"] = "PSP",
        ["NPEZ"] = "PSP",
        ["NPJZ"] = "PSP",
    };

    private static readonly HashSet<string> NonTitlePrefixes = new(StringComparer.Ordinal)
    {
        "SUBC", "SCEA", "NPIA", "NPUP", "NPEP", "NPJP", "NPUK", "NPEK", "NPXS", "PSNP",
    };

    private static readonly Dictionary<string, string> PlatformIdAliases = new(StringComparer.Ordinal)
    {
        ["ps5"] = "PS5",
        ["ps4"] = "PS4",
        ["ps3"] = "PS3",
        ["psvita"] = "PSVITA",
        ["psp"] = "PSP",
    };

    public static string? PlatformForTitleId(string? titleId)
    {
        if (Prefix(titleId) is not { } prefix || NonTitlePrefixes.Contains(prefix))
        {
            return null;
        }

        return PlatformByTitleIdPrefix.GetValueOrDefault(prefix);
    }

    public static bool IsNonTitleEntitlement(string? titleId) =>
        Prefix(titleId) is { } prefix && NonTitlePrefixes.Contains(prefix);

    public static string? NormalizePlatformId(string? platformId) =>
        platformId is null ? null : PlatformIdAliases.GetValueOrDefault(platformId.ToLowerInvariant());

    private static string? Prefix(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        return (titleId.Length <= 4 ? titleId : titleId[..4]).ToUpperInvariant();
    }
}
