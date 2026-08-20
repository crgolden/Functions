namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnCachedAccessToken
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("expires_in")]
    public double ExpiresIn { get; init; }

    [JsonPropertyName("access_token_expires_at")]
    public double AccessTokenExpiresAt { get; init; }
}
