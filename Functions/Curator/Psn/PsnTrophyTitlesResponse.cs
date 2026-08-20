namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnTrophyTitlesResponse
{
    private readonly IReadOnlyList<PsnTrophyTitle> _trophyTitles = [];

    [JsonPropertyName("trophyTitles")]
    public IReadOnlyList<PsnTrophyTitle> TrophyTitles
    {
        get => _trophyTitles;
        init => _trophyTitles = value ?? [];
    }

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; init; }

    [JsonPropertyName("totalItemCount")]
    public int? TotalItemCount { get; init; }
}

public sealed record PsnTitleTrophyTitlesResponse
{
    private readonly IReadOnlyList<PsnTitleTrophyTitles> _titles = [];

    [JsonPropertyName("titles")]
    public IReadOnlyList<PsnTitleTrophyTitles> Titles
    {
        get => _titles;
        init => _titles = value ?? [];
    }
}

public sealed record PsnTitleTrophyTitles
{
    private readonly IReadOnlyList<PsnTrophyTitle> _trophyTitles = [];

    [JsonPropertyName("npTitleId")]
    public string? NpTitleId { get; init; }

    [JsonPropertyName("trophyTitles")]
    public IReadOnlyList<PsnTrophyTitle> TrophyTitles
    {
        get => _trophyTitles;
        init => _trophyTitles = value ?? [];
    }
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
