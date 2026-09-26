namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record PsnTitleTrophyTitles
{
    [JsonPropertyName("npTitleId")]
    public string? NpTitleId { get; init; }

    [JsonPropertyName("trophyTitles")]
    [AllowNull]
    public IReadOnlyList<PsnTrophyTitle> TrophyTitles { get => field; init => field = value ?? []; } = [];
}
