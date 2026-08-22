namespace Functions.Curator.Enrichment;

using Rawg;

public readonly record struct RawgLookup(RawgGameDetail? Detail, bool Attempted)
{
    public static RawgLookup NeverAsked => new(null, Attempted: false);

    public static RawgLookup Answered(RawgGameDetail? detail) => new(detail, Attempted: true);
}
