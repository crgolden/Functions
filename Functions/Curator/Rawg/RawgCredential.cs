namespace Functions.Curator.Rawg;

public sealed record RawgCredential
{
    internal const string RedactedPlaceholder = "[redacted]";

    public required string ApiKey { get; init; }

    public string Redact(string text) =>
        text.Replace(ApiKey, RedactedPlaceholder, StringComparison.Ordinal);
}
