namespace Functions.Curator.Store;

public sealed record StoreCrawlCategory(
    Guid CategoryId,
    string Platform,
    string ReportingNamePrefix,
    int NextOffset,
    DateTimeOffset? WalkCompletedAt);
