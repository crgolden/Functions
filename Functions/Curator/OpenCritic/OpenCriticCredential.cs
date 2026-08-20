namespace Functions.Curator.OpenCritic;

public sealed record OpenCriticCredential
{
    required public string RapidApiKey { get; init; }

    public string Redact(string text) =>
        text.Replace(RapidApiKey, "[redacted]", StringComparison.Ordinal);
}
