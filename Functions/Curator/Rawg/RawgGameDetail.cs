namespace Functions.Curator.Rawg;

using System.Text.Json.Serialization;

public sealed record RawgNamed
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record RawgGameDetail
{
    [JsonPropertyName("genres")]
    public IReadOnlyList<RawgNamed> Genres
    {
        get => field;
        init => field = value ?? [];
    } = [];

    [JsonPropertyName("developers")]
    public IReadOnlyList<RawgNamed> Developers
    {
        get => field;
        init => field = value ?? [];
    } = [];

    [JsonPropertyName("publishers")]
    public IReadOnlyList<RawgNamed> Publishers
    {
        get => field;
        init => field = value ?? [];
    } = [];

    [JsonPropertyName("tags")]
    public IReadOnlyList<RawgNamed> Tags
    {
        get => field;
        init => field = value ?? [];
    } = [];

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
