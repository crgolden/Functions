namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnCachedAccessToken
{
    public const string AccessTokenPropertyName = "access_token";
    public const string ExpiresInPropertyName = "expires_in";
    public const string AccessTokenExpiresAtPropertyName = "access_token_expires_at";

    [JsonPropertyName(AccessTokenPropertyName)]
    public string? AccessToken { get; init; }

    [JsonPropertyName(ExpiresInPropertyName)]
    public double ExpiresIn { get; init; }

    [JsonPropertyName(AccessTokenExpiresAtPropertyName)]
    public double AccessTokenExpiresAt { get; init; }
}
