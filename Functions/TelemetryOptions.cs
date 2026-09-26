namespace Functions;

public sealed record TelemetryOptions(
    string ExceptionsDescription,
    string GeocoderFallbacksDescription,
    string ZipBackfillDescription,
    string BulkImportRowsDescription,
    string ReGeocodedChurchesDescription,
    string ReGeocodedCampusesDescription,
    string EnrichmentGateUnavailableDescription,
    string EnrichmentGamesDescription,
    string ProviderDisabledDescription,
    string StaleRedeliveriesDescription,
    string TransientRetriesDescription,
    string ReapedLeasesDescription,
    string OpenCriticSweepGamesDescription,
    string PsnSessionRotationsDescription,
    string StoreProductsDescription,
    string QueueActiveDescription,
    string QueueDeadLetterDescription);
