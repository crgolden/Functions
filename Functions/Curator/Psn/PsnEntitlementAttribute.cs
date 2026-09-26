namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnEntitlementAttribute
{
    [JsonPropertyName("platformId")]
    public string? PlatformId { get; init; }
}
