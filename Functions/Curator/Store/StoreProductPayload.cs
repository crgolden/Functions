namespace Functions.Curator.Store;

using System.Text.Json.Serialization;

public sealed record StoreGraphResponse
{
    [JsonPropertyName("data")]
    public StoreGraphData? Data { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<StoreGraphError> Errors
    {
        get => field;
        init => field = value ?? [];
    } = [];
}

public sealed record StoreGraphData
{
    [JsonPropertyName("productRetrieve")]
    public StoreProductNode? ProductRetrieve { get; init; }
}

public sealed record StoreGraphError
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("extensions")]
    public StoreGraphErrorExtensions? Extensions { get; init; }
}

public sealed record StoreGraphErrorExtensions
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }
}

public sealed record StoreProductNode
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("npTitleId")]
    public string? NpTitleId { get; init; }

    [JsonPropertyName("publisherName")]
    public string? PublisherName { get; init; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("contentRating")]
    public StoreContentRating? ContentRating { get; init; }

    [JsonPropertyName("combinedLocalizedGenres")]
    public IReadOnlyList<StoreLocalizedGenre> Genres
    {
        get => field;
        init => field = value ?? [];
    } = [];

    [JsonPropertyName("concept")]
    public StoreConcept? Concept { get; init; }

    [JsonPropertyName("starRating")]
    public StoreStarRating? StarRating { get; init; }
}

public sealed record StoreContentRating
{
    [JsonPropertyName("authority")]
    public string? Authority { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record StoreLocalizedGenre
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

public sealed record StoreConcept
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

public sealed record StoreStarRating
{
    [JsonPropertyName("averageRating")]
    public double? AverageRating { get; init; }

    [JsonPropertyName("totalRatingsCount")]
    public int? TotalRatingsCount { get; init; }
}
