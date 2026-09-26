namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record PsnTitleTrophyTitlesResponse
{
    [JsonPropertyName("titles")]
    [AllowNull]
    public IReadOnlyList<PsnTitleTrophyTitles> Titles { get => field; init => field = value ?? []; } = [];
}
