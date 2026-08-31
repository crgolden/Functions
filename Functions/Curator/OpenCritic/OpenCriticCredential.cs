namespace Functions.Curator.OpenCritic;

public sealed record OpenCriticCredential
{
    internal const string RedactedPlaceholder = "[redacted]";

    required public string RapidApiKey { get; init; }

    public string Redact(string text) =>
        text.Replace(RapidApiKey, RedactedPlaceholder, StringComparison.Ordinal);
}
