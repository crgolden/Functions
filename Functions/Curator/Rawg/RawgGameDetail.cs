namespace Functions.Curator.Rawg;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record RawgGameDetail
{
    [JsonPropertyName("genres")]
    [AllowNull]
    public IReadOnlyList<RawgNamed> Genres { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName("developers")]
    [AllowNull]
    public IReadOnlyList<RawgNamed> Developers { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName("publishers")]
    [AllowNull]
    public IReadOnlyList<RawgNamed> Publishers { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName("tags")]
    [AllowNull]
    public IReadOnlyList<RawgNamed> Tags { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName("metacritic")]
    public double? Metacritic { get; init; }

    [JsonPropertyName("released")]
    public string? Released { get; init; }

    [JsonPropertyName("esrb_rating")]
    public RawgNamed? EsrbRating { get; init; }

    public IReadOnlyList<string> NamesOf(Func<RawgGameDetail, IReadOnlyList<RawgNamed>> selector) =>
        selector(this).Select(entry => entry.Name).OfType<string>().ToList();
}
