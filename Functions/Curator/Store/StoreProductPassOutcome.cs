namespace Functions.Curator.Store;

public sealed record StoreProductPassOutcome(int Enriched, int Unavailable, int Remaining, string? StoppedReason);
