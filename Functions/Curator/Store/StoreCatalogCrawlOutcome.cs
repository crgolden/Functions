namespace Functions.Curator.Store;

using JetBrains.Annotations;

[PublicAPI]
public sealed record StoreCatalogCrawlOutcome(
    int PagesRead,
    int ProductsSeen,
    int GamesCreated,
    string? StoppedReason);
