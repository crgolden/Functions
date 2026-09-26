namespace Functions.Curator.Psn;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record PsnConceptMedia
{
    [JsonPropertyName("images")]
    [AllowNull]
    public IReadOnlyList<PsnConceptImage> Images { get => field; init => field = value ?? []; } = [];
}
