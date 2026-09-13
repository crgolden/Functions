namespace Functions.Curator.Catalog;

public static class ContentKinds
{
    public const string Game = "game";

    public const string MediaApp = "media_app";

    public const string MediaAppPackageType = "PSMEDIA";

    public const string ApplicationConceptType = "APPLICATION";

    public static string? FromPackageType(string? packageType) => packageType switch
    {
        MediaAppPackageType => MediaApp,
        "PS4GD" or "PSGD" => Game,
        _ => null,
    };
}
