namespace Functions.Tests.Unit.TestSupport;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Functions.Curator.Library;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Npgsql;

internal static class TestValues
{
    internal static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    internal static string NewGameTitle() => $"{LowercaseToken(5)} {LowercaseToken(8)}";

    internal static string NewTokenFromFirstHalfOfAlphabet(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'n')));

    internal static string NewTokenFromSecondHalfOfAlphabet(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('n', 'z' + 1)));

    internal static string NewToken() => $"token-{Guid.NewGuid():N}";

    internal static string NewNormalizedTitle() => LowercaseToken(12);

    internal static string NewLongTitle() => NewTokenFromFirstHalfOfAlphabet(24);

    internal static string WithAnEditionSuffix(string title) =>
        $"{title} {NewTokenFromSecondHalfOfAlphabet(title.Length / 4)}";

    internal static string NewPublisher() => $"{LowercaseToken(6)} {LowercaseToken(9)}";

    internal static string NewGenre() => LowercaseToken(9).ToUpperInvariant();

    internal static string NewTagWithoutAMultiplayerKeyword() => NewTokenFromFirstHalfOfAlphabet(9);

    internal static string NewFranchiseName() => $"{LowercaseToken(4)} {LowercaseToken(7)}";

    internal static int NewRulePriority() => Random.Shared.Next(1, 100);

    internal static string NewFingerprint() => $"fingerprint-{Guid.NewGuid():N}";

    internal static string NewOpenCriticTier() => $"tier-{LowercaseToken(8)}";

    internal static string NewContentRating() => $"rating-{LowercaseToken(6)}";

    internal static string NewRatingAuthority() => $"authority-{LowercaseToken(8)}";

    internal static string NewCoverImageUrl() => $"https://{LowercaseToken(10)}.example/{LowercaseToken(8)}.png";

    internal static Uri NewCoverImageUri() => new(NewCoverImageUrl(), UriKind.Absolute);

    internal static string NewTitleId() => NewTitleIdWithSerial(Random.Shared.Next(10000, 100000));

    internal static string NewTitleIdWithSerial(int serial) => $"{TrophyMatchService.Ps4TitleIdPrefix}{serial}_00";

    internal static string NewPs5TitleId() => $"PPSA{Random.Shared.Next(10000, 100000)}_00";

    internal static IReadOnlyList<string> NewDistinctTitleIds(int count)
    {
        var firstSerial = Random.Shared.Next(10000, 100000 - count);
        return [.. Enumerable.Range(0, count).Select(offset => NewTitleIdWithSerial(firstSerial + offset))];
    }

    internal static int NewConceptNumericId() => Random.Shared.Next(1, 100_000_000);

    internal static string NewConceptType() => $"concepttype-{LowercaseToken(6)}";

    internal static string NewReleaseDateType() => $"releasetype-{LowercaseToken(6)}";

    internal static string NewContentRatingDescription() => $"rated {LowercaseToken(8)}";

    internal static string NewImageType() => $"imagetype-{LowercaseToken(6)}";

    internal static string NewCompatibilityNoticeType() => $"noticetype-{LowercaseToken(6)}";

    internal static string NewNonNumericToken() => $"count-{LowercaseToken(8)}";

    internal static int NewMultiplayerPlayerCount() => Random.Shared.Next(2, 100);

    internal static int NewMinimumAge() => Random.Shared.Next(0, 21);

    internal static DateTimeOffset NewReleaseTimestamp() =>
        new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero)
            .AddDays(-Random.Shared.Next(1, 3_650));

    internal static int NewExpiresInSeconds() => Random.Shared.Next(60, 86_400);

    internal static int NewRateLimitMaxRequests() => Random.Shared.Next(2, 20);

    internal static double NewRateLimitWindowSeconds() => Random.Shared.Next(30, 900);

    internal static TimeSpan NewNonZeroUtcOffset() => TimeSpan.FromHours(Random.Shared.Next(1, 13));

    internal static IReadOnlyList<string> NewGenreList(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => NewGenre())];

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

    internal static string NewSkuId() => $"sku-{Guid.NewGuid():N}";

    internal static string NewPackageType() => $"package-{LowercaseToken(4)}";

    internal static string NewGameType() => $"gametype-{LowercaseToken(4)}";

    internal static string NewPlatformId() => $"platform-{LowercaseToken(4)}";

    internal static long NewDownloadSizeBytes() => Random.Shared.NextInt64(1, 50L * 1024 * 1024 * 1024);

    internal static string NewPs3EntitlementId() =>
        $"UP{DigitToken(4)}-BLUS{Random.Shared.Next(10000, 100000)}_00-{LowercaseToken(16).ToUpperInvariant()}";

    internal static string NewNonTitleEntitlementId() =>
        $"IP{DigitToken(4)}-NPIA{Random.Shared.Next(10000, 100000)}_00-{LowercaseToken(16).ToUpperInvariant()}";

    internal static string NewEntitlementIdPrefix() => $"{LowercaseToken(6)}-";

    internal static string NewJsonPropertyName() => LowercaseToken(9);

    internal static string NewBlankRun() => new(' ', Random.Shared.Next(1, 4));

    internal static DateTimeOffset NewTimestampWithNonZeroOffset() =>
        new DateTimeOffset(
            2000 + Random.Shared.Next(1, 26),
            Random.Shared.Next(1, 13),
            Random.Shared.Next(1, 28),
            Random.Shared.Next(0, 24),
            Random.Shared.Next(0, 60),
            Random.Shared.Next(0, 60),
            TimeSpan.FromHours(Random.Shared.Next(1, 13)));

    internal static string NewNpCommunicationId() => $"NPWR{Random.Shared.Next(10000, 100000)}_00";

    internal static string NewAccessToken() => $"access-{Guid.NewGuid():N}";

    internal static string NewRefreshToken() => $"refresh-{Guid.NewGuid():N}";

    internal static string NewNpsso() => $"npsso-{Guid.NewGuid():N}";

    internal static string NewAuthorizationCode() => $"code-{Guid.NewGuid():N}";

    internal static string NewUrlPath() => $"path-{Guid.NewGuid():N}";

    internal static string NewRapidApiKey() => $"rapidapi-key-{Guid.NewGuid():N}";

    internal static string NewRawgApiKey() => $"rawg-key-{Guid.NewGuid():N}";

    internal static string NewOpenAIApiKey() => $"openai-key-{Guid.NewGuid():N}";

    internal static string NewRedisPassword() => $"redis-password-{Guid.NewGuid():N}";

    internal static string NewResendApiToken() => $"resend-token-{Guid.NewGuid():N}";

    internal static byte[] NewTokenCryptoRawKey()
    {
        var raw = new byte[TokenCrypto.KeySizeBytes];
        RandomNumberGenerator.Fill(raw);
        return raw;
    }

    internal static string NewTokenCryptoKey() =>
        Convert.ToBase64String(NewTokenCryptoRawKey()).Replace('+', '-').Replace('/', '_');

    internal static string NewPostgresConnectionString() =>
        new NpgsqlConnectionStringBuilder
        {
            Host = NewHostLabel(),
            Database = NewLettersOnlyToken(),
            Username = NewLettersOnlyToken(),
            Password = NewToken(),
        }.ConnectionString;

    internal static Uri NewProviderBaseAddress() =>
        new UriBuilder(Uri.UriSchemeHttps, NewHostLabel()) { Path = "/" }.Uri;

    internal static Uri NewProviderBaseAddressUnderAPathPrefix() =>
        new UriBuilder(Uri.UriSchemeHttps, NewHostLabel()) { Path = $"/{LowercaseToken(4)}/" }.Uri;

    internal static int NewRetryAfterSeconds() => Random.Shared.Next(1, 600);

    internal static int NewPaginationCursor() =>
        Random.Shared.Next(1, 200) * OpenCriticClient.DefaultPageSize;

    internal static Uri NewPsnUri(string? path = null) =>
        new UriBuilder(Uri.UriSchemeHttps, PsnSession.AllowedHosts.First())
        {
            Path = path ?? NewUrlPath(),
        }.Uri;

    internal static Uri NewInsecurePsnUri() =>
        new UriBuilder(Uri.UriSchemeHttp, PsnSession.AllowedHosts.First())
        {
            Path = NewUrlPath(),
        }.Uri;

    internal static Uri NewPsnUriWithTraversal() =>
        new UriBuilder(Uri.UriSchemeHttps, PsnSession.AllowedHosts.First())
        {
            Path = $"{NewUrlPath()}/../../{NewUrlPath()}",
        }.Uri;

    internal static Uri NewUriOnHost(string host) =>
        new UriBuilder(Uri.UriSchemeHttps, host) { Path = NewUrlPath() }.Uri;

    internal static string NewUpstreamErrorBody() => $"upstream-failure-{Guid.NewGuid():N}";

    internal static string NewRejectionMessage() => $"rejected-{Guid.NewGuid():N}";

    internal static int NewRefreshTokenExpiresInSeconds() => Random.Shared.Next(86_400, 5_184_000);

    internal static string NewGameName() => $"Game {Guid.NewGuid():N}";

    internal static string NewIdentitySub() => Guid.NewGuid().ToString();

    internal static int NewRawgGameId() => Random.Shared.Next(1, 1_000_000);

    internal static double NewOpenCriticScore() => Random.Shared.Next(0, 1001) / 10.0;

    internal static int NewOpenCriticGameId() => Random.Shared.Next(1, 1_000_000);

    internal static double NewCriticScore() => Math.Round(Random.Shared.NextDouble() * 100.0, 2);

    internal static double NewPercentRecommended() => Math.Round(Random.Shared.NextDouble() * 100.0, 2);

    internal static double NewStarRating() => Math.Round(Random.Shared.NextDouble() * 5.0, 2);

    internal static int NewPsnRatingCount() => Random.Shared.Next(1, 1_000_000);

    internal static string NewStoreProductId() => $"UP{DigitToken(4)}-CUSA{Random.Shared.Next(10000, 100000)}_00-{LowercaseToken(16).ToUpperInvariant()}";

    internal static string NewGenreDisplayName() => $"{LowercaseToken(6)} {LowercaseToken(7)}";

    internal static DateOnly NewReleaseDate() =>
        new DateOnly(2000, 1, 1).AddDays(Random.Shared.Next(0, 9_000));

    internal static int NewReleaseYear() => Random.Shared.Next(1990, 2030);

    internal static string NewErrorMessage() => $"failure-{LowercaseToken(10)}";

    internal static string NewSettingKey() => $"Setting{Guid.NewGuid():N}";

    internal static string NewContributorId() => $"user{Guid.NewGuid():N}";

    internal static string NewFieldName() => $"field{Guid.NewGuid():N}";

    internal static string NewFieldValue() => $"value{Guid.NewGuid():N}";

    internal static string NewHostLabel() => $"example-{Guid.NewGuid():N}.test";

    internal static int NewPortNumber() => Random.Shared.Next(1024, 65535);

    internal static string NewEmailSubject() => $"Subject {Guid.NewGuid():N}";

    internal static string NewHtmlBody() => $"<p>{Guid.NewGuid():N}</p>";

    internal static string NewGroupKey() => $"group-{Guid.NewGuid():N}";

    internal static string NewGameId() => Guid.NewGuid().ToString();

    internal static int NewTrophyProgress() => Random.Shared.Next(1, 100);

    internal static string NewPublisherPattern() => LowercaseToken(8);

    internal static DateTimeOffset NewUtcTimestamp() =>
        DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(1, 100000));

    internal static string NewChurchName() => $"church{LowercaseToken(12)}";

    internal static string NewNonLatinChurchName() =>
        string.Concat(Enumerable.Range(0, 8).Select(_ => (char)Random.Shared.Next(0x4E00, 0x9FFF)));

    internal static string NewStreetName() => $"{Guid.NewGuid():N} Street";

    internal static string NewHouseNumber() =>
        Random.Shared.Next(100, 9999).ToString(CultureInfo.InvariantCulture);

    internal static string NewImportBlobPath() => $"{Guid.NewGuid():N}/{Guid.NewGuid():N}";

    internal static string WithATrailingLetter(string name) => $"{name}{NewPaddingChar()}";

    internal static int NewChurchCountSharingABucket() => Random.Shared.Next(2, 41);

    internal static double NewOffsetWithinHalfACell() => Random.Shared.Next(1, 50) / 100.0;

    internal static string NewLettersOnlyToken() => LowercaseToken(8);

    internal static string NewDigitsOnlyToken() => DigitToken(8);

    internal static string NewDigitsOnlyName() => DigitToken(16);

    internal static string DigitToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('0', '9' + 1)));

    internal static string NewCampusName() => $"campus{LowercaseToken(12)}";

    internal static string NewCity() => $"city{LowercaseToken(12)}";

    internal static string NewFullStateName() => $"state{LowercaseToken(10)}";

    internal static string NewNteeCode() =>
        $"X{Random.Shared.Next(10, 100).ToString(CultureInfo.InvariantCulture)}";

    internal static byte NewEarlyWeekDayOfWeek() => (byte)Random.Shared.Next(0, 3);

    internal static byte NewLateWeekDayOfWeek() => (byte)Random.Shared.Next(3, 7);

    internal static byte NewDayOfWeek() => (byte)Random.Shared.Next(0, 7);

    internal static byte NewInvalidDayOfWeek() => (byte)Random.Shared.Next(7, 256);

    internal static char NewPaddingChar() => (char)Random.Shared.Next('a', 'z' + 1);

    internal static int NewOverflowMargin() => Random.Shared.Next(1, 100);

    internal static decimal NewConfidence() => Math.Round((decimal)Random.Shared.NextDouble(), 2);

    internal static int NewWorshipStyle() => Random.Shared.Next(1, 6);

    internal static string NewStateCode() =>
        $"{(char)Random.Shared.Next('A', 'Z' + 1)}{(char)Random.Shared.Next('A', 'Z' + 1)}";

    internal static string NewOverlongZipDigits() =>
        Random.Shared.NextInt64(100_000_000_000L, 1_000_000_000_000L)
            .ToString(CultureInfo.InvariantCulture);

    internal static string NewZip() =>
        Random.Shared.Next(10000, 100000).ToString(CultureInfo.InvariantCulture);

    internal static string NewStreet() =>
        $"{Random.Shared.Next(100, 10000).ToString(CultureInfo.InvariantCulture)} {LowercaseToken(10)} street";

    internal static string NewPhoneNumber() =>
        $"{Random.Shared.Next(200, 1000)}-{Random.Shared.Next(200, 1000)}-{Random.Shared.Next(1000, 10000)}";

    internal static string NewParenthesizedPhoneNumber() =>
        $"({Random.Shared.Next(200, 1000)}) {Random.Shared.Next(200, 1000)}-{Random.Shared.Next(1000, 10000)}";

    internal static string NewProseWithoutAPhoneNumber() =>
        $"{LowercaseToken(6)} {LowercaseToken(7)} {LowercaseToken(10)}";

    internal static string NewBlobPath() => $"{LowercaseToken(2)}/{LowercaseToken(10)}.html";

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

    internal static Guid NewCrawlSourceId() => Guid.NewGuid();

    internal static decimal NewOutOfRangeLatitude() => Random.Shared.Next(91, 1000);

    internal static int NewOutOfRangeWorshipStyle() => Random.Shared.Next(6, 1000);

    internal static TimeSpan NewJobTimeBudgetAllowance() => TimeSpan.FromSeconds(Random.Shared.Next(60, 3_600));

    internal static int NewJobRunSeq() => Random.Shared.Next(0, 1000);

    internal static string NewRawgReleasedText() =>
        NewReleaseDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static double NewMetacriticScore() => Random.Shared.Next(1, 101);

    internal static int NewRawgPlatformId() => Random.Shared.Next(1, 1_000);

    internal static string NewRawgPlatformName() => $"platform{Guid.NewGuid():N}";

    internal static string NewEsrbRatingName() => $"esrb{Guid.NewGuid():N}";

    internal static string NewProviderErrorBody() => JsonSerializer.Serialize(new { detail = NewErrorMessage() });

    internal static string NewOpenCriticRawPayload() => JsonSerializer.Serialize(new { id = NewOpenCriticGameId() });

    internal static string NewUnknownSslModeSpelling() => $"sslmode{Guid.NewGuid():N}";

    internal static string NewPostgresIdentifier() => $"id{Guid.NewGuid():N}";

    internal static byte[] NewCiphertext() => Guid.NewGuid().ToByteArray();

    internal static long NewAccessTokenExpiry() => 1_700_000_000 + Random.Shared.Next(1, 100_000);

    internal static long NewRefreshTokenExpiry() => 1_800_000_000 + Random.Shared.Next(1, 100_000);

    internal static int NewReGeocodeBatchSize() => Random.Shared.Next(10, 500);

    internal static Guid NewRunId() => Guid.NewGuid();

    internal static int NewConsecutiveFailureCount() => Random.Shared.Next(2, 20);

    internal static string NewHtmlDocument() => $"<html><h1>{Guid.NewGuid():N}</h1></html>";

    internal static string NewPlaintextSecret() => $"secret-{Guid.NewGuid():N}";
}
