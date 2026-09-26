namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record PsnConceptPayload
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; init; }

    [JsonPropertyName("minimumAge")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? MinimumAge { get; init; }

    [JsonPropertyName("releaseDate")]
    public PsnReleaseDate? ReleaseDate { get; init; }

    [JsonPropertyName("contentRating")]
    public PsnContentRating? ContentRating { get; init; }

    [JsonPropertyName("starRating")]
    public PsnStarRating? StarRating { get; init; }

    [JsonPropertyName("genres")]
    [AllowNull]
    public IReadOnlyList<string> Genres { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName("titleIds")]
    [AllowNull]
    public IReadOnlyList<string> TitleIds { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName("media")]
    public PsnConceptMedia? Media { get; init; }

    [JsonPropertyName("compatibilityNotices")]
    [AllowNull]
    public IReadOnlyList<PsnCompatibilityNotice> CompatibilityNotices { get => field; init => field = value ?? []; } = [];
}
