namespace Functions.Curator.Psn;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record PsnEntitlementsResponse
{
    private readonly IReadOnlyList<JsonElement> _entitlements = [];

    [JsonPropertyName("totalResults")]
    public int TotalResults { get; init; }

    [JsonPropertyName("entitlements")]
    public IReadOnlyList<JsonElement> Entitlements
    {
        get => _entitlements;
        init => _entitlements = value ?? [];
    }
}

public sealed record PsnEntitlementPayload
{
    private readonly IReadOnlyList<PsnEntitlementAttribute> _entitlementAttributes = [];

    [JsonPropertyName("id")]
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
        get => _entitlementAttributes;
        init => _entitlementAttributes = value ?? [];
    }
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
