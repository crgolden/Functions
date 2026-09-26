namespace Functions.Curator.Store;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

public sealed record StoreCategoryGrid
{
    [JsonPropertyName("reportingName")]
    public string? ReportingName { get; init; }

    [JsonPropertyName("pageInfo")]
    public StoreCategoryPageInfo? PageInfo { get; init; }

    [JsonPropertyName("products")]
    [AllowNull]
    public IReadOnlyList<StoreCategoryProduct> Products { get => field; init => field = value ?? []; } = [];
}
