namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;
using Functions.Curator.Jobs;
using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record EnrichmentRunMessage : ICuratorJobMessage
{
    [JsonPropertyName("run_id")]
    public required Guid RunId { get; init; }

    [JsonPropertyName("seq")]
    public int Seq { get; init; }
}
