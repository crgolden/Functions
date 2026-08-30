namespace Functions.Curator.Enrichment;

using Rawg;

public readonly record struct RawgLookup(RawgGameDetail? Detail, bool Attempted)
{
    public static RawgLookup NeverAsked => new(null, Attempted: false);

    public static RawgLookup Answered(RawgGameDetail? detail) => new(detail, Attempted: true);

    public static RawgLookup ReusedFromCache(RawgGameDetail? detail) => new(detail, Attempted: false);

    public bool DetailResolved => Attempted && Detail is not null;
}
