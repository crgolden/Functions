namespace Functions.Curator.Store;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record StoreCategoryProduct
{
    public const string FullGameClassification = "Full Game";

    internal const string IdJsonName = "id";

    internal const string NameJsonName = "name";

    internal const string NpTitleIdJsonName = "npTitleId";

    internal const string ClassificationJsonName = "localizedStoreDisplayClassification";

    internal const string PlatformsJsonName = "platforms";

    internal const string MediaJsonName = "media";

    internal const string PriceJsonName = "price";

    [JsonPropertyName(IdJsonName)]
    public string? Id { get; init; }

    [JsonPropertyName(NameJsonName)]
    public string? Name { get; init; }

    [JsonPropertyName(NpTitleIdJsonName)]
    public string? NpTitleId { get; init; }

    [JsonPropertyName(ClassificationJsonName)]
    public string? Classification { get; init; }

    [JsonPropertyName(PlatformsJsonName)]
    [AllowNull]
    public IReadOnlyList<string> Platforms { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName(MediaJsonName)]
    [AllowNull]
    public IReadOnlyList<StoreMediaEntry> Media { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName(PriceJsonName)]
    public StoreCategoryPrice? Price { get; init; }

    [JsonExtensionData]
    [AllowNull]
    public IDictionary<string, JsonElement> Unmapped { get => field; init => field = value ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal); } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public bool IsFullGame => string.Equals(Classification, FullGameClassification, StringComparison.Ordinal);

    public string? CoverImageUrl => StoreCoverArt.Best(Media);
}
