namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnTitleMeta
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("titleId")]
    public string? TitleId { get; init; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; init; }
}
