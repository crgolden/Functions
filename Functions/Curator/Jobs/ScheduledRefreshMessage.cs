namespace Functions.Curator.Jobs;

using System.Text.Json.Serialization;

public sealed record ScheduledRefreshMessage(
    [property: JsonPropertyName("identity_sub")] Guid IdentitySub,
    [property: JsonPropertyName("scheduled_for")] DateTimeOffset ScheduledFor);
