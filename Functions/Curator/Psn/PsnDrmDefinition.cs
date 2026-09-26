namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record PsnDrmDefinition
{
    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("drmContents")]
    [AllowNull]
    public IReadOnlyList<PsnDrmContent> Contents { get => field; init => field = value ?? []; } = [];
}
