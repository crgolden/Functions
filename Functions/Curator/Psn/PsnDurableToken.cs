namespace Functions.Curator.Psn;

using System.Text.Json.Serialization;

public sealed record PsnDurableToken
{
    public const string RefreshTokenPropertyName = "refresh_token";
    public const string RefreshTokenExpiresAtPropertyName = "refresh_token_expires_at";

    [JsonPropertyName(RefreshTokenPropertyName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; init; }

    [JsonPropertyName(RefreshTokenExpiresAtPropertyName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RefreshTokenExpiresAt { get; init; }
}
