namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnStarRating
{
    [JsonPropertyName("score")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? Score { get; init; }
}
