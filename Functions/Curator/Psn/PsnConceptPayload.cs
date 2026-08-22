namespace Functions.Curator.Psn;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record PsnConceptPayload
{
    private readonly IReadOnlyList<string> _genres = [];
    private readonly IReadOnlyList<string> _titleIds = [];
    private readonly IReadOnlyList<PsnCompatibilityNotice> _compatibilityNotices = [];

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
    public IReadOnlyList<string> Genres
    {
        get => _genres;
        init => _genres = value ?? [];
    }

    [JsonPropertyName("titleIds")]
    public IReadOnlyList<string> TitleIds
    {
        get => _titleIds;
        init => _titleIds = value ?? [];
    }

    [JsonPropertyName("media")]
    public PsnConceptMedia? Media { get; init; }

    [JsonPropertyName("compatibilityNotices")]
    public IReadOnlyList<PsnCompatibilityNotice> CompatibilityNotices
    {
        get => _compatibilityNotices;
        init => _compatibilityNotices = value ?? [];
    }
}

public sealed record PsnReleaseDate
{
    [JsonPropertyName("date")]
    public DateTimeOffset? Date { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed record PsnContentRating
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("authority")]
    public string? Authority { get; init; }
}

public sealed record PsnStarRating
{
    [JsonPropertyName("score")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? Score { get; init; }
}

public sealed record PsnConceptMedia
{
    private readonly IReadOnlyList<PsnConceptImage> _images = [];

    [JsonPropertyName("images")]
    public IReadOnlyList<PsnConceptImage> Images
    {
        get => _images;
        init => _images = value ?? [];
    }
}

public sealed record PsnConceptImage
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed record PsnCompatibilityNotice
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }
}
