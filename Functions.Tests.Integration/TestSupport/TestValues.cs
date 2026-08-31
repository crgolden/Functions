namespace Functions.Tests.Integration.TestSupport;

internal static class TestValues
{
    internal static string NewTitle() => Guid.NewGuid().ToString();

    internal static string NewTitle(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    internal static string NewConceptId() =>
        Random.Shared.Next(10_000_000, 100_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string NewTitleId() =>
        $"CUSA{Random.Shared.Next(10000, 100000).ToString(System.Globalization.CultureInfo.InvariantCulture)}_00";

    internal static string NewEntitlementId() => $"entitlement-{Guid.NewGuid():N}";

    internal static string NewNpCommunicationId() =>
        $"NPWR{Random.Shared.Next(10000, 100000).ToString(System.Globalization.CultureInfo.InvariantCulture)}_00";

    internal static string NewOwnedEdition() => $"edition-{LowercaseToken(8)}";

    internal static int NewTrophyProgress() => Random.Shared.Next(1, 100);

    internal static string NewProductId() => $"product-{Guid.NewGuid():N}";

    internal static string NewFingerprint() => $"fingerprint-{Guid.NewGuid():N}";

    internal static string NewFranchiseKeyword() => Guid.NewGuid().ToString("N");

    internal static string NewFranchiseName() => $"Franchise {Guid.NewGuid():N}";

    internal static string NewOverrideName() => $"Override {Guid.NewGuid():N}";

    internal static string NewToken() => $"token-{Guid.NewGuid():N}";

    internal static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    internal static string NewGameTitle() => $"{LowercaseToken(5)} {LowercaseToken(8)}";

    internal static string NewGenre() => LowercaseToken(9).ToUpperInvariant();

    internal static string NewPublisher() => $"{LowercaseToken(6)} {LowercaseToken(9)}";

    internal static string NewOpenCriticTier() => $"tier-{LowercaseToken(8)}";

    internal static int NewOpenCriticGameId() => Random.Shared.Next(1, 50000);

    internal static string NewContentRating() => $"rating-{LowercaseToken(6)}";

    internal static string NewRatingAuthority() => $"authority-{LowercaseToken(8)}";

    internal static string NewCoverImageUrl() => $"https://{LowercaseToken(10)}.example/{LowercaseToken(8)}.png";

    internal static DateTimeOffset NewUtcTimestamp() =>
        DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(1, 100000));

    internal static double NewCriticScore() => Math.Round(Random.Shared.NextDouble() * 100.0, 2);

    internal static double NewPercentRecommended() => Math.Round(Random.Shared.NextDouble() * 100.0, 2);

    internal static double NewStarRating() => Math.Round(Random.Shared.NextDouble() * 5.0, 2);

    internal static DateOnly NewReleaseDate() =>
        new DateOnly(2000, 1, 1).AddDays(Random.Shared.Next(0, 9_000));

    internal static int NewReleaseYear() => Random.Shared.Next(1990, 2030);
}
