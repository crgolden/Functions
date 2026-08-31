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

    internal static string NewProductId() => $"product-{Guid.NewGuid():N}";

    internal static string NewFingerprint() => $"fingerprint-{Guid.NewGuid():N}";

    internal static string NewFranchiseKeyword() => Guid.NewGuid().ToString("N");

    internal static string NewFranchiseName() => $"Franchise {Guid.NewGuid():N}";

    internal static string NewOverrideName() => $"Override {Guid.NewGuid():N}";

    internal static string NewToken() => $"token-{Guid.NewGuid():N}";

    internal static double NewCriticScore() => Math.Round(Random.Shared.NextDouble() * 100.0, 2);

    internal static double NewPercentRecommended() => Math.Round(Random.Shared.NextDouble() * 100.0, 2);

    internal static double NewStarRating() => Math.Round(Random.Shared.NextDouble() * 5.0, 2);

    internal static DateOnly NewReleaseDate() =>
        new DateOnly(2000, 1, 1).AddDays(Random.Shared.Next(0, 9_000));

    internal static int NewReleaseYear() => Random.Shared.Next(1990, 2030);
}
