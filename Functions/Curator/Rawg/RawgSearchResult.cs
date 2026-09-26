namespace Functions.Curator.Rawg;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record RawgSearchResult
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("released")]
    public string? Released { get; init; }

    [JsonPropertyName("metacritic")]
    public double? Metacritic { get; init; }

    [JsonPropertyName("esrb_rating")]
    public RawgNamed? EsrbRating { get; init; }

    [JsonPropertyName("platforms")]
    [AllowNull]
    public IReadOnlyList<RawgSearchPlatformEntry> Platforms { get => field; init => field = value ?? []; } = [];
}
