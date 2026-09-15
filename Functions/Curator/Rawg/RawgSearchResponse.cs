namespace Functions.Curator.Rawg;

using System.Text.Json.Serialization;
using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record RawgSearchPlatform
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record RawgSearchPlatformEntry
{
    [JsonPropertyName("platform")]
    public RawgSearchPlatform? Platform { get; init; }
}

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
    public IReadOnlyList<RawgSearchPlatformEntry> Platforms { get => field; init => field = value ?? []; } = [];
}

public sealed record RawgSearchResponse
{
    [JsonPropertyName("results")]
    public IReadOnlyList<RawgSearchResult> Results { get => field; init => field = value ?? []; } = [];
}
