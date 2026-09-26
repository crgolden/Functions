namespace Functions.Churches.Crawling;

internal sealed record ScrapeRequest(Guid CrawlSourceId, string Url);
