namespace Functions.Curator.Rawg;

using System.Text.Json.Serialization;

public sealed record RawgNamed
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record RawgGameDetail
{
    private readonly IReadOnlyList<RawgNamed> _genres = [];
    private readonly IReadOnlyList<RawgNamed> _developers = [];
    private readonly IReadOnlyList<RawgNamed> _publishers = [];
    private readonly IReadOnlyList<RawgNamed> _tags = [];

    [JsonPropertyName("genres")]
    public IReadOnlyList<RawgNamed> Genres
    {
        get => _genres;
        init => _genres = value ?? [];
    }

    [JsonPropertyName("developers")]
    public IReadOnlyList<RawgNamed> Developers
    {
        get => _developers;
        init => _developers = value ?? [];
    }

    [JsonPropertyName("publishers")]
    public IReadOnlyList<RawgNamed> Publishers
    {
        get => _publishers;
        init => _publishers = value ?? [];
    }

    [JsonPropertyName("tags")]
    public IReadOnlyList<RawgNamed> Tags
    {
        get => _tags;
        init => _tags = value ?? [];
    }

    [JsonPropertyName("metacritic")]
    public double? Metacritic { get; init; }

    [JsonPropertyName("released")]
    public string? Released { get; init; }

    [JsonPropertyName("esrb_rating")]
    public RawgNamed? EsrbRating { get; init; }

    public IReadOnlyList<string> NamesOf(Func<RawgGameDetail, IReadOnlyList<RawgNamed>> selector) =>
        selector(this).Select(entry => entry.Name).OfType<string>().ToList();
}

public sealed record RawgGameDetailResponse(RawgGameDetail Detail, string Raw);
