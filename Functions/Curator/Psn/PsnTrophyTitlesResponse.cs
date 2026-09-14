namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnTrophyTitlesResponse
{
    [JsonPropertyName("trophyTitles")]
    public IReadOnlyList<PsnTrophyTitle> TrophyTitles
    {
        get => field;
        init => field = value ?? [];
    } = [];

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; init; }

    [JsonPropertyName("totalItemCount")]
    public int? TotalItemCount { get; init; }
}

public sealed record PsnTitleTrophyTitlesResponse
{
    [JsonPropertyName("titles")]
    public IReadOnlyList<PsnTitleTrophyTitles> Titles
    {
        get => field;
        init => field = value ?? [];
    } = [];
}

public sealed record PsnTitleTrophyTitles
{
    [JsonPropertyName("npTitleId")]
    public string? NpTitleId { get; init; }

    [JsonPropertyName("trophyTitles")]
    public IReadOnlyList<PsnTrophyTitle> TrophyTitles
    {
        get => field;
        init => field = value ?? [];
    } = [];
}

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
