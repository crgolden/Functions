namespace Functions.Tests.Unit.TestSupport;

using System.Globalization;

internal static class TestValues
{
    internal static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    internal static string NewGameTitle() => $"{LowercaseToken(5)} {LowercaseToken(8)}";

    internal static string NewTokenFromFirstHalfOfAlphabet(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'n')));

    internal static string NewTokenFromSecondHalfOfAlphabet(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('n', 'z' + 1)));

    internal static string NewNormalizedTitle() => LowercaseToken(12);

    internal static string NewLongTitle() => NewTokenFromFirstHalfOfAlphabet(24);

    internal static string WithAnEditionSuffix(string title) =>
        $"{title} {NewTokenFromSecondHalfOfAlphabet(title.Length / 4)}";

    internal static string NewPublisher() => $"{LowercaseToken(6)} {LowercaseToken(9)}";

    internal static string NewGenre() => LowercaseToken(9).ToUpperInvariant();

    internal static string NewFranchiseName() => $"{LowercaseToken(4)} {LowercaseToken(7)}";

    internal static int NewRulePriority() => Random.Shared.Next(1, 100);

    internal static string NewFingerprint() => $"fingerprint-{Guid.NewGuid():N}";

    internal static string NewOpenCriticTier() => $"tier-{LowercaseToken(8)}";

    internal static string NewContentRating() => $"rating-{LowercaseToken(6)}";

    internal static string NewRatingAuthority() => $"authority-{LowercaseToken(8)}";

    internal static string NewCoverImageUrl() => $"https://{LowercaseToken(10)}.example/{LowercaseToken(8)}.png";

    internal static string NewTitleId() => $"CUSA{Random.Shared.Next(10000, 100000)}_00";

    internal static string NewPs5TitleId() => $"PPSA{Random.Shared.Next(10000, 100000)}_00";

    internal static IReadOnlyList<string> NewDistinctTitleIds(int count)
    {
        var firstSerial = Random.Shared.Next(10000, 100000 - count);
        return [.. Enumerable.Range(0, count).Select(offset => $"CUSA{firstSerial + offset}_00")];
    }

    internal static string NewConceptId() =>
        Random.Shared.Next(10_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture);

    internal static string NewConceptIdSortingFirst() =>
        Random.Shared.Next(10_000_000, 50_000_000).ToString(CultureInfo.InvariantCulture);

    internal static string NewConceptIdSortingLast() =>
        Random.Shared.Next(50_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture);

    internal static string NewOverrideName() => $"override {LowercaseToken(10)}";

    internal static string NewEditionKeyword() => $"{LowercaseToken(4)} {LowercaseToken(6)}";

    internal static int NewEditionRank() => Random.Shared.Next(1, 10);

    internal static string NewEntitlementId() => $"entitlement-{Guid.NewGuid():N}";

    internal static string NewProductId() => $"product-{Guid.NewGuid():N}";

    internal static string NewNpCommunicationId() => $"NPWR{Random.Shared.Next(10000, 100000)}_00";

    internal static string NewAccessToken() => $"access-{Guid.NewGuid():N}";

    internal static string NewRefreshToken() => $"refresh-{Guid.NewGuid():N}";

    internal static string NewNpsso() => $"npsso-{Guid.NewGuid():N}";

    internal static string NewAuthorizationCode() => $"code-{Guid.NewGuid():N}";

    internal static string NewGameName() => $"Game {Guid.NewGuid():N}";

    internal static string NewIdentitySub() => Guid.NewGuid().ToString();

    internal static int NewRawgGameId() => Random.Shared.Next(1, 1_000_000);

    internal static double NewOpenCriticScore() => Random.Shared.Next(0, 1001) / 10.0;

    internal static int NewOpenCriticGameId() => Random.Shared.Next(1, 50000);

    internal static double NewCriticScore() => Math.Round(Random.Shared.NextDouble() * 100.0, 2);

    internal static double NewPercentRecommended() => Math.Round(Random.Shared.NextDouble() * 100.0, 2);

    internal static double NewStarRating() => Math.Round(Random.Shared.NextDouble() * 5.0, 2);

    internal static DateOnly NewReleaseDate() =>
        new DateOnly(2000, 1, 1).AddDays(Random.Shared.Next(0, 9_000));

    internal static int NewReleaseYear() => Random.Shared.Next(1990, 2030);

    internal static string NewErrorMessage() => $"failure-{LowercaseToken(10)}";

    internal static DateTimeOffset NewUtcTimestamp() =>
        DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(1, 100000));

    internal static string NewChurchName() => $"church{LowercaseToken(12)}";

    internal static string NewCampusName() => $"campus{LowercaseToken(12)}";

    internal static string NewCity() => $"city{LowercaseToken(12)}";

    internal static string NewStateCode() =>
        $"{(char)Random.Shared.Next('A', 'Z' + 1)}{(char)Random.Shared.Next('A', 'Z' + 1)}";

    internal static string NewZip() =>
        Random.Shared.Next(10000, 100000).ToString(CultureInfo.InvariantCulture);

    internal static string NewStreet() =>
        $"{Random.Shared.Next(100, 10000).ToString(CultureInfo.InvariantCulture)} {LowercaseToken(10)} street";

    internal static string NewPhoneNumber() =>
        $"{Random.Shared.Next(200, 1000)}-{Random.Shared.Next(200, 1000)}-{Random.Shared.Next(1000, 10000)}";

    internal static string NewEmailAddress() => $"{LowercaseToken(10)}@{LowercaseToken(10)}.example";

    internal static string NewWebsite() => $"https://{LowercaseToken(12)}.example";

    internal static string NewHost() => $"host{LowercaseToken(12)}.example";

    internal static string NewMinistryName() => $"ministry{LowercaseToken(12)}";

    internal static string NewMinistryDescription() => $"description{LowercaseToken(12)}";

    internal static string NewServiceDescription() => $"service{LowercaseToken(12)}";

    internal static string NewDenominationName() => $"denomination{LowercaseToken(12)}";

    internal static string NewLanguageName() => $"language{LowercaseToken(8)}";

    internal static string NewServiceTime() =>
        $"{Random.Shared.Next(0, 24):D2}:{Random.Shared.Next(0, 60):D2}";

    internal static decimal NewGeocodedLatitude() =>
        Math.Round(((decimal)Random.Shared.NextDouble() * 40m) + 1m, 4);

    internal static decimal NewGeocodedLongitude() =>
        -Math.Round(((decimal)Random.Shared.NextDouble() * 100m) + 1m, 4);

    internal static double NewScoredLatitude() => Math.Round((Random.Shared.NextDouble() * 40) + 1, 4);

    internal static double NewScoredLongitude() => -Math.Round((Random.Shared.NextDouble() * 100) + 1, 4);
}
