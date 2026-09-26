namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record PsnEntitlementsResponse
{
    internal const string TotalResultsPropertyName = "totalResults";
    internal const string EntitlementsPropertyName = "entitlements";

    [JsonPropertyName(TotalResultsPropertyName)]
    public int? TotalResults { get; init; }

    [JsonPropertyName(EntitlementsPropertyName)]
    [AllowNull]
    public IReadOnlyList<JsonElement> Entitlements { get => field; init => field = value ?? []; } = [];
}
