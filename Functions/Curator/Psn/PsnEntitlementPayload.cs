namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

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
    [AllowNull]
    public IReadOnlyList<PsnEntitlementAttribute> EntitlementAttributes { get => field; init => field = value ?? []; } = [];
}
