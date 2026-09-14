namespace Functions.Curator.Psn;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record PsnEntitlementsResponse
{
    internal const string TotalResultsPropertyName = "totalResults";
    internal const string EntitlementsPropertyName = "entitlements";

    [JsonPropertyName(TotalResultsPropertyName)]
    public int? TotalResults { get; init; }

    [JsonPropertyName(EntitlementsPropertyName)]
    public IReadOnlyList<JsonElement> Entitlements
    {
        get => field;
        init => field = value ?? [];
    } = [];
}

public sealed record PsnEntitlementPayload
{
    internal const string IdPropertyName = "id";

    [JsonPropertyName(IdPropertyName)]
    public string? Id { get; init; }

    [JsonPropertyName("productId")]
    public string? ProductId { get; init; }

    [JsonPropertyName("skuId")]
    public string? SkuId { get; init; }

    [JsonPropertyName("activeFlag")]
    public bool? ActiveFlag { get; init; }

    [JsonPropertyName("activeDate")]
    public DateTimeOffset? ActiveDate { get; init; }

    [JsonPropertyName("isGame")]
    public bool? IsGame { get; init; }

    [JsonPropertyName("gameMeta")]
    public PsnGameMeta? GameMeta { get; init; }

    [JsonPropertyName("titleMeta")]
    public PsnTitleMeta? TitleMeta { get; init; }

    [JsonPropertyName("conceptMeta")]
    public PsnConceptMeta? ConceptMeta { get; init; }

    [JsonPropertyName("entitlementAttributes")]
    public IReadOnlyList<PsnEntitlementAttribute> EntitlementAttributes
    {
        get => field;
        init => field = value ?? [];
    } = [];
}

public sealed record PsnGameMeta
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("packageType")]
    public string? PackageType { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; init; }
}

public sealed record PsnTitleMeta
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("titleId")]
    public string? TitleId { get; init; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }
}

public sealed record PsnConceptMeta
{
    [JsonPropertyName("conceptId")]
    public string? ConceptId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; init; }
}

public sealed record PsnEntitlementAttribute
{
    [JsonPropertyName("platformId")]
    public string? PlatformId { get; init; }
}
