namespace Functions.Curator.Store;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record StoreMediaEntry
{
    internal const string RoleJsonName = "role";

    internal const string TypeJsonName = "type";

    internal const string UrlJsonName = "url";

    [JsonPropertyName(RoleJsonName)]
    public string? Role { get; init; }

    [JsonPropertyName(TypeJsonName)]
    public string? Type { get; init; }

    [JsonPropertyName(UrlJsonName)]
    public string? Url { get; init; }

    [JsonExtensionData]
    [AllowNull]
    public IDictionary<string, JsonElement> Unmapped { get => field; init => field = value ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal); } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
