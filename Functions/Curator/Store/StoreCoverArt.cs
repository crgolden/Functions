namespace Functions.Curator.Store;

public static class StoreCoverArt
{
    public const string ImageMediaType = "IMAGE";

    public static readonly string[] RolePreference =
    [
        "GAMEHUB_COVER_ART",
        "EDITION_KEY_ART",
        "PORTRAIT_BANNER",
        "BACKGROUND",
    ];

    public static Uri? Best(IReadOnlyList<StoreMediaEntry> media)
    {
        foreach (var role in RolePreference)
        {
            var match = media.FirstOrDefault(entry =>
                string.Equals(entry.Type, ImageMediaType, StringComparison.Ordinal)
                && string.Equals(entry.Role, role, StringComparison.Ordinal)
                && entry.Url is not null);
            if (match is not null)
            {
                return match.Url;
            }
        }

        return null;
    }
}
