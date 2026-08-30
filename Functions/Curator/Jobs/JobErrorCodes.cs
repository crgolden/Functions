namespace Functions.Curator.Jobs;

public static class JobErrorCodes
{
    public const string PsnLinkExpired = "psn_link_expired";

    public const string ProviderKeyRejected = "provider_key_rejected";

    public const string ProviderRateLimited = "provider_rate_limited";

    public const string MalformedPayload = "malformed_payload";

    public const string Unexpected = "unexpected";

    public const string Abandoned = "abandoned";
}
