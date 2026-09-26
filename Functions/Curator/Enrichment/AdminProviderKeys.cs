namespace Functions.Curator.Enrichment;

public sealed record AdminProviderKeys(
    IReadOnlyList<string> RawgApiKeys,
    IReadOnlyList<string> OpenCriticRapidApiKeys,
    IReadOnlyList<string> PsnNpssoTokens);
