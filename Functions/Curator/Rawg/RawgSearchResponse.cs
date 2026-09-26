namespace Functions.Curator.Rawg;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record RawgSearchResponse
{
    [JsonPropertyName("results")]
    [AllowNull]
    public IReadOnlyList<RawgSearchResult> Results { get => field; init => field = value ?? []; } = [];
}
