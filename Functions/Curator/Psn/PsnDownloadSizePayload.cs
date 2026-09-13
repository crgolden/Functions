namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnCommerceEntitlementsResponse
{
    private readonly IReadOnlyList<PsnCommerceEntitlement> _entitlements = [];

    [JsonPropertyName("total_results")]
    public int? TotalResults { get; init; }

    [JsonPropertyName("entitlements")]
    public IReadOnlyList<PsnCommerceEntitlement> Entitlements
    {
        get => _entitlements;
        init => _entitlements = value ?? [];
    }
}

public sealed record PsnCommerceEntitlement
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("drm_def")]
    public PsnDrmDefinition? DrmDefinition { get; init; }
}

public sealed record PsnDrmDefinition
{
    private readonly IReadOnlyList<PsnDrmContent> _contents = [];

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("drmContents")]
    public IReadOnlyList<PsnDrmContent> Contents
    {
        get => _contents;
        init => _contents = value ?? [];
    }
}

public sealed record PsnDrmContent
{
    [JsonPropertyName("contentSize")]
    public long? ContentSize { get; init; }
}
