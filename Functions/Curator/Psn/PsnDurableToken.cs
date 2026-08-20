namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnDurableToken
{
    [JsonPropertyName("refresh_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("refresh_token_expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RefreshTokenExpiresAt { get; init; }
}
