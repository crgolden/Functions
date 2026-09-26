namespace Functions.Curator.Store;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record StoreGraphResponse
{
    [JsonPropertyName("data")]
    public StoreGraphData? Data { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("errors")]
    [AllowNull]
    public IReadOnlyList<StoreGraphError> Errors { get => field; init => field = value ?? []; } = [];
}
