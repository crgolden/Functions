namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnTrophyTitle
{
    [JsonPropertyName("npCommunicationId")]
    public string? NpCommunicationId { get; init; }

    [JsonPropertyName("trophyTitleName")]
    public string? TrophyTitleName { get; init; }

    [JsonPropertyName("progress")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Progress { get; init; }
}
