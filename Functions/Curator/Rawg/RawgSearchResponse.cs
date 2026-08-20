namespace Functions.Curator.Rawg;

using System.Text.Json.Serialization;

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
    private readonly IReadOnlyList<RawgSearchPlatformEntry> _platforms = [];

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
    public IReadOnlyList<RawgSearchPlatformEntry> Platforms
    {
        get => _platforms;
        init => _platforms = value ?? [];
    }
}

public sealed record RawgSearchResponse
{
    private readonly IReadOnlyList<RawgSearchResult> _results = [];

    [JsonPropertyName("results")]
    public IReadOnlyList<RawgSearchResult> Results
    {
        get => _results;
        init => _results = value ?? [];
    }
}
