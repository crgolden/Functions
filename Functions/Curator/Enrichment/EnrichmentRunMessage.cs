namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Jobs;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record EnrichmentRunMessage : ICuratorJobMessage
{
    [JsonPropertyName("run_id")]
    required public Guid RunId { get; init; }

    [JsonPropertyName("seq")]
    public int Seq { get; init; }
}
