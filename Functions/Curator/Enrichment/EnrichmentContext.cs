namespace Functions.Curator.Enrichment;

public sealed record EnrichmentContext(EnrichmentOrchestrationService Service, EnrichmentCredentials Credentials);
