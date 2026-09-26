namespace Functions.Curator.Store;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

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
    [JsonConverter(typeof(UnparseableAsNullTimestampConverter))]
    public DateTimeOffset? ReleaseDate { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("contentRating")]
    public StoreContentRating? ContentRating { get; init; }

    [JsonPropertyName("combinedLocalizedGenres")]
    [AllowNull]
    public IReadOnlyList<StoreLocalizedGenre> Genres { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName("concept")]
    public StoreConcept? Concept { get; init; }

    [JsonPropertyName("starRating")]
    public StoreStarRating? StarRating { get; init; }
}
