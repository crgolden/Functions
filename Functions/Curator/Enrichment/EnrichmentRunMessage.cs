namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;
using Jobs;

public sealed record EnrichmentRunMessage : ICuratorJobMessage
{
    [JsonPropertyName("run_id")]
    required public string RunId { get; init; }

    [JsonPropertyName("seq")]
    public int Seq { get; init; }
}
