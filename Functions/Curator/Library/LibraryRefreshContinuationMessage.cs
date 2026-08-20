namespace Functions.Curator.Library;

using System.Text.Json.Serialization;
using Jobs;

public sealed record LibraryRefreshContinuationMessage : ICuratorJobMessage
{
    [JsonPropertyName("run_id")]
    required public string RunId { get; init; }

    [JsonPropertyName("identity_sub")]
    required public string IdentitySub { get; init; }

    [JsonPropertyName("remaining_game_ids")]
    required public IReadOnlyList<string> RemainingGameIds { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("retry_after_seconds")]
    required public double RetryAfterSeconds { get; init; }

    [JsonPropertyName("seq")]
    public int Seq { get; init; }
}
