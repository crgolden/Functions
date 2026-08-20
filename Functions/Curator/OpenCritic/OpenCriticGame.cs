namespace Functions.Curator.OpenCritic;

public sealed record OpenCriticGame(
    int OcGameId,
    string Name,
    double? TopCriticScore,
    string? Tier,
    double? PercentRecommended)
{
    public string? Raw { get; init; }

    public bool Equals(OpenCriticGame? other) =>
        other is not null
        && OcGameId == other.OcGameId
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Nullable.Equals(TopCriticScore, other.TopCriticScore)
        && string.Equals(Tier, other.Tier, StringComparison.Ordinal)
        && Nullable.Equals(PercentRecommended, other.PercentRecommended);

    public override int GetHashCode() =>
        HashCode.Combine(OcGameId, Name, TopCriticScore, Tier, PercentRecommended);
}
