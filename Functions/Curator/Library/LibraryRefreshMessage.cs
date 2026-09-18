namespace Functions.Curator.Library;

using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Jobs;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LibraryRefreshMessage : ICuratorJobMessage
{
    [JsonPropertyName("run_id")]
    required public Guid RunId { get; init; }

    [JsonPropertyName("identity_sub")]
    required public Guid IdentitySub { get; init; }

    [JsonPropertyName("seq")]
    public int Seq { get; init; }
}
