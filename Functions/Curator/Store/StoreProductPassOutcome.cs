namespace Functions.Curator.Store;

using JetBrains.Annotations;

[PublicAPI]
public sealed record StoreProductPassOutcome(int Enriched, int Unavailable, int Remaining, string? StoppedReason);
