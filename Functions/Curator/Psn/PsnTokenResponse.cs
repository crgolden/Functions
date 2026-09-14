namespace Functions.Curator.Psn;

using JetBrains.Annotations;

[PublicAPI]
public sealed record PsnTokenResponse
{
    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    required public double ExpiresIn { get; init; }

    public double? RefreshTokenExpiresIn { get; init; }

    required public double AccessTokenExpiresAt { get; init; }

    public double? RefreshTokenExpiresAt { get; init; }
}
