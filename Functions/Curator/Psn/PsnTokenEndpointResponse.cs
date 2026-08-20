namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnTokenEndpointResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token_expires_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RefreshTokenExpiresIn { get; init; }
}
