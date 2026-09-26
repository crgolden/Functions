namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record PsnCommerceEntitlementsResponse
{
    [JsonPropertyName("total_results")]
    public int? TotalResults { get; init; }

    [JsonPropertyName("entitlements")]
    [AllowNull]
    public IReadOnlyList<PsnCommerceEntitlement> Entitlements { get => field; init => field = value ?? []; } = [];
}
