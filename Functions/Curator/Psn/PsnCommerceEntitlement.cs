namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnCommerceEntitlement
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("drm_def")]
    public PsnDrmDefinition? DrmDefinition { get; init; }
}
