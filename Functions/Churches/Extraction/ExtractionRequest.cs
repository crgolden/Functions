namespace Functions.Churches.Extraction;

internal sealed record ExtractionRequest(Guid CrawlSourceId, string BlobPath, string Url);
