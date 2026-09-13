namespace Functions.Curator.Psn;

public static class TitlePlatform
{
    public const string Ps5 = "PS5";

    public const string Ps4 = "PS4";

    public const string Ps3 = "PS3";

    public const string PsVita = "PSVITA";

    public const string Psp = "PSP";

    public const string Ps5PlatformId = "ps5";

    public const string Ps4PlatformId = "ps4";

    public const string Ps3PlatformId = "ps3";

    public const string PsVitaPlatformId = "psvita";

    public const string PspPlatformId = "psp";

    private static readonly Dictionary<string, string> PlatformByTitleIdPrefix = new(StringComparer.Ordinal)
    {
        ["PPSA"] = Ps5,
        ["CUSA"] = Ps4,
        ["BLUS"] = Ps3,
        ["BLES"] = Ps3,
        ["BLJM"] = Ps3,
        ["BLJS"] = Ps3,
        ["BCUS"] = Ps3,
        ["BCES"] = Ps3,
        ["BCJS"] = Ps3,
        ["BCAS"] = Ps3,
        ["NPUB"] = Ps3,
        ["NPEB"] = Ps3,
        ["NPJB"] = Ps3,
        ["NPHB"] = Ps3,
        ["NPUA"] = Ps3,
        ["NPEA"] = Ps3,
        ["NPJA"] = Ps3,
        ["NPHA"] = Ps3,
        ["NPUO"] = Ps3,
        ["NPEO"] = Ps3,
        ["NPUX"] = Ps3,
        ["NPEX"] = Ps3,
        ["PCSA"] = PsVita,
        ["PCSB"] = PsVita,
        ["PCSC"] = PsVita,
        ["PCSD"] = PsVita,
        ["PCSE"] = PsVita,
        ["PCSF"] = PsVita,
        ["PCSG"] = PsVita,
        ["PCSH"] = PsVita,
        ["VLUS"] = PsVita,
        ["VCUS"] = PsVita,
        ["VCJS"] = PsVita,
        ["VCAS"] = PsVita,
        ["NPVA"] = PsVita,
        ["NPVB"] = PsVita,
        ["NPVC"] = PsVita,
        ["NPVX"] = PsVita,
        ["UCUS"] = Psp,
        ["UCES"] = Psp,
        ["UCJS"] = Psp,
        ["UCAS"] = Psp,
        ["ULUS"] = Psp,
        ["ULES"] = Psp,
        ["ULJM"] = Psp,
        ["ULJS"] = Psp,
        ["NPUG"] = Psp,
        ["NPEG"] = Psp,
        ["NPJG"] = Psp,
        ["NPHG"] = Psp,
        ["NPUZ"] = Psp,
        ["NPEZ"] = Psp,
        ["NPJZ"] = Psp,
    };

    private static readonly HashSet<string> NonTitlePrefixes = new(StringComparer.Ordinal)
    {
        "SUBC", "SCEA", "NPIA", "NPUP", "NPEP", "NPJP", "NPUK", "NPEK", "NPXS", "PSNP",
    };

    private static readonly Dictionary<string, string> PlatformIdAliases = new(StringComparer.Ordinal)
    {
        [Ps5PlatformId] = Ps5,
        [Ps4PlatformId] = Ps4,
        [Ps3PlatformId] = Ps3,
        [PsVitaPlatformId] = PsVita,
        [PspPlatformId] = Psp,
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
