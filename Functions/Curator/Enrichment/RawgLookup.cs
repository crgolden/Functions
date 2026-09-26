namespace Functions.Curator.Enrichment;

using Functions.Curator.Rawg;

public readonly record struct RawgLookup(RawgGameDetail? Detail, bool Attempted)
{
    public static RawgLookup NeverAsked => new(null, Attempted: false);

    public bool DetailResolved => Attempted && Detail is not null;

    public static RawgLookup Answered(RawgGameDetail? detail) => new(detail, Attempted: true);

    public static RawgLookup ReusedFromCache(RawgGameDetail? detail) => new(detail, Attempted: false);
}
