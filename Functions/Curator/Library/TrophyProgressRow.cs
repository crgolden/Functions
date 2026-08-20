namespace Functions.Curator.Library;

using System.Text.Json.Serialization;

public sealed record TrophyProgressRow
{
    [JsonPropertyName("np_communication_id")]
    public string? NpCommunicationId { get; init; }

    [JsonPropertyName("percent")]
    public int Percent { get; init; }
}
