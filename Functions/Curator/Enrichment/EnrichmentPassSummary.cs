namespace Functions.Curator.Enrichment;

using System.Text.Json.Serialization;
using JetBrains.Annotations;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record EnrichmentPassSummary(
    [property: JsonPropertyName(EnrichmentRunSummaryFields.Providers)] IReadOnlyDictionary<string, string> Providers,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.AttemptedCount)] int AttemptedCount,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.EnrichedCount)] int EnrichedCount,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.RemainingCount)] int RemainingCount,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.StoppedProvider)] string? StoppedProvider,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.StoppedReason)] string? StoppedReason,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.RejectedProviders)] IReadOnlyList<string> RejectedProviders,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.UnavailableProviders)] IReadOnlyList<string> UnavailableProviders,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.RawgEnrichedCount)] int RawgEnrichedCount = 0,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.OpenCriticEnrichedCount)] int OpenCriticEnrichedCount = 0,
    [property: JsonPropertyName(EnrichmentRunSummaryFields.PsnEnrichedCount)] int PsnEnrichedCount = 0);
