namespace Functions.Curator.Psn;

using JetBrains.Annotations;

[PublicAPI]
public sealed record PsnTokenResponse
{
    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public required double ExpiresIn { get; init; }

    public double? RefreshTokenExpiresIn { get; init; }

    public required double AccessTokenExpiresAt { get; init; }

    public double? RefreshTokenExpiresAt { get; init; }
}
