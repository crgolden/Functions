namespace Functions.Curator.Catalog;

public static class ContentKinds
{
    public const string Game = "game";

    public const string MediaApp = "media_app";

    public const string MediaAppPackageType = "PSMEDIA";

    public const string Ps4GamePackageType = "PS4GD";

    public const string Ps5GamePackageType = "PSGD";

    public const string ApplicationConceptType = "APPLICATION";

    public static ContentKind? FromPackageType(string? packageType) => packageType switch
    {
        MediaAppPackageType => ContentKind.MediaApp,
        Ps4GamePackageType or Ps5GamePackageType => ContentKind.Game,
        _ => null,
    };

    public static string ToWireName(this ContentKind kind) => kind switch
    {
        ContentKind.Game => Game,
        ContentKind.MediaApp => MediaApp,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
