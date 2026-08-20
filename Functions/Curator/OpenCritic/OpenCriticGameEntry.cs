namespace Functions.Curator.OpenCritic;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record OpenCriticGameEntry
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("topCriticScore")]
    public double? TopCriticScore { get; init; }

    [JsonPropertyName("tier")]
    public string? Tier { get; init; }

    [JsonPropertyName("percentRecommended")]
    public double? PercentRecommended { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement> AdditionalFields { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public OpenCriticGame? ToGame(string raw)
    {
        if (Id is not { } id || string.IsNullOrWhiteSpace(Name))
        {
            return null;
        }

        return new OpenCriticGame(
            id,
            Name,
            NullIfNoData(TopCriticScore),
            Tier,
            NullIfNoData(PercentRecommended))
        {
            Raw = raw,
        };
    }

    private static double? NullIfNoData(double? value) => value < 0 ? null : value;
}
