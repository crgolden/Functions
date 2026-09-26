namespace Functions.Curator.Library;

using System.Text.Json.Serialization;
using Functions.Curator.Jobs;
using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LibraryRefreshMessage : ICuratorJobMessage
{
    [JsonPropertyName("run_id")]
    public required Guid RunId { get; init; }

    [JsonPropertyName("identity_sub")]
    public required Guid IdentitySub { get; init; }

    [JsonPropertyName("seq")]
    public int Seq { get; init; }
}
