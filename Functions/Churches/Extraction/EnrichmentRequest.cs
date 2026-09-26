namespace Functions.Churches.Extraction;

internal sealed record EnrichmentRequest(Guid CrawlSourceId, string Url, string? PageText, EnrichmentPartialData Partial);
