namespace Functions.Curator.Library;

using System.Text.Json.Serialization;
using Functions.Curator.Jobs;

public sealed record LibraryRefreshContinuationMessage : ICuratorJobMessage
{
    [JsonPropertyName("run_id")]
    public required Guid RunId { get; init; }

    [JsonPropertyName("identity_sub")]
    public required Guid IdentitySub { get; init; }

    [JsonPropertyName("remaining_game_ids")]
    public required IReadOnlyList<Guid> RemainingGameIds { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("retry_after_seconds")]
    public required double RetryAfterSeconds { get; init; }

    [JsonPropertyName("seq")]
    public int Seq { get; init; }
}
