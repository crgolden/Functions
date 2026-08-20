namespace Functions.Curator.Rawg;

public sealed record RawgCredential
{
    internal const string RedactedPlaceholder = "[redacted]";

    required public string ApiKey { get; init; }

    public string Redact(string text) =>
        text.Replace(ApiKey, RedactedPlaceholder, StringComparison.Ordinal);
}
