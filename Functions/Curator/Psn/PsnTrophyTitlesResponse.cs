namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record PsnTrophyTitlesResponse
{
    [JsonPropertyName("trophyTitles")]
    [AllowNull]
    public IReadOnlyList<PsnTrophyTitle> TrophyTitles { get => field; init => field = value ?? []; } = [];

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; init; }

    [JsonPropertyName("totalItemCount")]
    public int? TotalItemCount { get; init; }
}
