namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Functions.Curator.Psn;
using Functions.Tests.Unit.TestSupport;
using static Functions.Tests.Unit.PsnCatalogClientFixtureConstants;

[Trait("Category", "Unit")]
public sealed class PsnCatalogClientTests
{
    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task TitleConceptAsync_ParsesTheConceptPayload()
    {
        // Arrange
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var conceptNumericId = Generated.NewConceptNumericId();
        var name = Generated.NewGameName();
        var publisherName = Generated.NewPublisher();
        var releaseDate = Generated.NewReleaseTimestamp();
        var minimumAge = Generated.NewMinimumAge();
        var contentRatingSymbolicName = Generated.NewContentRating();
        var ratingAuthority = Generated.NewRatingAuthority();
        var starRating = Generated.NewStarRating();
        var genres = Generated.NewGenreList(Generated.NewMultiGenreCount());
        var titleIds = NewTitleIds();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = conceptNumericId,
                Name = name,
                Type = Generated.NewConceptType(),
                PublisherName = publisherName,
                MinimumAge = minimumAge,
                ReleaseDate = new PsnReleaseDate
                {
                    Date = releaseDate,
                    Type = Generated.NewReleaseDateType(),
                },
                ContentRating = new PsnContentRating { Name = contentRatingSymbolicName, Authority = ratingAuthority },
                StarRating = new PsnStarRating { Score = starRating },
                Genres = genres,
                TitleIds = titleIds,
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, titleId, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(conceptNumericId.ToString(CultureInfo.InvariantCulture), concept.ConceptId);
        Assert.Equal(name, concept.Name);
        Assert.Equal(publisherName, concept.Publisher);
        Assert.Equal(releaseDate, concept.ReleaseDate);
        Assert.Equal(minimumAge, concept.MinimumAge);
        Assert.Equal(contentRatingSymbolicName, concept.ContentRating);
        Assert.Equal(ratingAuthority, concept.RatingAuthority);
        Assert.Equal(starRating, concept.StarRating);
        Assert.Equal(genres, concept.Genres);
        Assert.Equal(titleIds, concept.TitleIds);
    }

    [Fact]
    public async Task TitleConceptAsync_ReadsTheContentRatingSymbolicNameCuratorStored_NotItsDescription()
    {
        // Arrange
        var conceptNumericId = Generated.NewConceptNumericId();
        var contentRatingAuthority = Generated.NewRatingAuthority();
        var contentRatingDescription = Generated.NewContentRatingDescription();
        var contentRatingSymbolicName = Generated.NewContentRating();
        var body = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = conceptNumericId,
                contentRating = new
                {
                    authority = contentRatingAuthority,
                    description = contentRatingDescription,
                    name = contentRatingSymbolicName,
                },
            },
        });
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, body));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(contentRatingSymbolicName, concept.ContentRating);
        Assert.NotEqual(contentRatingDescription, concept.ContentRating);
    }

    [Fact]
    public async Task TitleConceptAsync_HasNoReleaseDate_WhenPsnPublishesOnlyAComingSoonLabel()
    {
        // Arrange
        var name = Generated.NewGameName();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                Name = name,
                ReleaseDate = new PsnReleaseDate { Type = Generated.NewReleaseDateType() },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(concept.ReleaseDate);
        Assert.Equal(name, concept.Name);
    }

    [Fact]
    public async Task TitleConceptAsync_NormalisesTheReleaseDateToUtc_WhenPsnSendsAnOffsetTimestamp()
    {
        // Arrange
        var releaseDateUtc = Generated.NewReleaseTimestamp();
        var sourceOffset = Generated.NewNonZeroUtcOffset();
        var releaseDateWithOffset = releaseDateUtc.ToOffset(sourceOffset);
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                ReleaseDate = new PsnReleaseDate { Date = releaseDateWithOffset },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(releaseDateUtc, concept.ReleaseDate);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(concept.ReleaseDate).Offset);
    }

    [Fact]
    public async Task TitleConceptAsync_RequestsTheAgeCountryAndLanguageQueryParametersPsnRequires()
    {
        // Arrange
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        await client.TitleConceptAsync(session, titleId, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ConceptsPathFor(titleId), request.RequestUri?.AbsolutePath);
        Assert.Contains(
            QueryPair(PsnCatalogClient.AgeQueryKey, PsnCatalogClient.AgeQueryValue),
            request.RequestUri?.Query,
            StringComparison.Ordinal);
        Assert.Contains(
            QueryPair(PsnCatalogClient.CountryQueryKey, PsnCatalogClient.CountryQueryValue),
            request.RequestUri?.Query,
            StringComparison.Ordinal);
        Assert.Contains(
            QueryPair(PsnCatalogClient.LanguageQueryKey, PsnCatalogClient.LanguageQueryValue),
            request.RequestUri?.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TitleConceptAsync_EscapesTheTitleIdIntoASinglePathSegment()
    {
        // Arrange
        var titleId = $"{Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix)}/{PsnSession.TraversalSegment}";
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        await client.TitleConceptAsync(session, titleId, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ConceptsPathFor(Uri.EscapeDataString(titleId)), request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenNoConceptsAreReturned_ReturnsAnEmptyTitleConcept()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(concept.ConceptId);
        Assert.Null(concept.Name);
        Assert.Empty(concept.Genres);
        Assert.Empty(concept.TitleIds);
    }

    [Fact]
    public async Task TitleConceptAsync_PicksCoverArtByRolePreferenceNotArrayOrder()
    {
        // Arrange
        var otherImageType = Generated.NewImageType();
        var preferredUrl = Generated.NewCoverImageUri();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        Image(otherImageType, Generated.NewCoverImageUri()),
                        Image(PsnCatalogClient.CoverImagePreference[0], preferredUrl),
                        Image(PsnCatalogClient.CoverImagePreference[1], Generated.NewCoverImageUri()),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(preferredUrl, concept.CoverImageUrl);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenOneImageTypeAppearsTwice_TheLastOneWins()
    {
        // Arrange
        var imageType = PsnCatalogClient.CoverImagePreference[0];
        var lastUrl = Generated.NewCoverImageUri();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        Image(imageType, Generated.NewCoverImageUri()),
                        Image(imageType, lastUrl),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(lastUrl, concept.CoverImageUrl);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenNoPreferredRoleIsPresent_FallsBackToTheFirstImageWithAUrl()
    {
        // Arrange
        var firstUrl = Generated.NewCoverImageUri();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        new PsnConceptImage { Type = Generated.NewImageType() },
                        Image(Generated.NewImageType(), firstUrl),
                        Image(Generated.NewImageType(), Generated.NewCoverImageUri()),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(firstUrl, concept.CoverImageUrl);
    }

    [Fact]
    public async Task TitleConceptAsync_MultiplayerIsNullWhenNoPlayerCountNoticeIsPublished()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                CompatibilityNotices =
                [
                    Notice(Generated.NewCompatibilityNoticeType(), true),
                    Notice(Generated.NewCompatibilityNoticeType(), Generated.NewNonNumericToken()),
                ],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_MultiplayerIsFalseWhenTheOnlyNoticeIsSinglePlayer()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfPlayersNoticeType, SinglePlayerOnly)],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.False(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_MultiplayerIsTrueWhenAnOnlineNetworkNoticeExceedsOne()
    {
        // Arrange
        var networkPlayerCount = Generated.NewMultiplayerPlayerCount();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                CompatibilityNotices =
                [
                    Notice(PsnCatalogClient.NoOfPlayersNoticeType, SinglePlayerOnly),
                    Notice(PsnCatalogClient.NoOfNetworkPlayersNoticeType, networkPlayerCount),
                    Notice(PsnCatalogClient.NoOfNetworkPlayersPsPlusNoticeType, networkPlayerCount),
                ],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_ReadsAPlayerCountPsnStringified()
    {
        // Arrange
        var networkPlayerCount = Generated.NewMultiplayerPlayerCount().ToString(CultureInfo.InvariantCulture);
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfNetworkPlayersNoticeType, networkPlayerCount)],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_IgnoresAPlayerCountNoticeThatCarriesNoNumber()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = Generated.NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfPlayersNoticeType, Generated.NewNonNumericToken())],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenTheCachedTokenIsRejectedAndAnNpssoIsAvailable_ReauthenticatesAndRetriesOnce()
    {
        // Arrange
        var npsso = Generated.NewNpsso();
        var cachedAccessToken = Generated.NewAccessToken();
        var authorizationCode = Generated.NewAuthorizationCode();
        var refreshedAccessToken = Generated.NewAccessToken();
        var recoveredName = Generated.NewGameName();
        var exchange = new[]
        {
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            RedirectTo(RedirectCarryingAuthorizationCode(authorizationCode)),
            TokenResponse(refreshedAccessToken),
            Json(HttpStatusCode.OK, Concepts(new PsnConceptPayload { Id = Generated.NewConceptNumericId(), Name = recoveredName })),
        };
        var handler = StubHttpMessageHandler.Sequence(exchange);
        var session = await PsnSession.RestoreAsync(
            npsso,
            SeededStore(cachedAccessToken),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(recoveredName, concept.Name);
        Assert.Equal(exchange.Length, handler.Requests.Count);
        Assert.Equal($"{PsnSession.BearerScheme} {cachedAccessToken}", handler.Requests[0].Headers.Authorization?.ToString());
        Assert.Equal($"{PsnSession.BearerScheme} {refreshedAccessToken}", handler.Requests[^1].Headers.Authorization?.ToString());
    }

    private static string Concepts(params PsnConceptPayload[] concepts) =>
        JsonSerializer.Serialize(concepts, PsnWireFormat);

    private static PsnConceptImage Image(string type, Uri url) => new() { Type = type, Url = url.OriginalString };

    private static PsnCompatibilityNotice Notice(string type, object value) =>
        new() { Type = type, Value = JsonSerializer.SerializeToElement(value) };

    private static async Task<PsnSession> ReadySessionAsync(StubHttpMessageHandler handler) =>
        await PsnSession.RestoreAsync(
            null,
            SeededStore(Generated.NewAccessToken()),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

    private static InMemoryPsnTokenStore SeededStore(string accessToken)
    {
        var store = new InMemoryPsnTokenStore();
        store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = accessToken,
                ExpiresIn = Generated.NewExpiresInSeconds(),
                AccessTokenExpiresAt = DateTimeOffset.UtcNow
                    .AddSeconds(Generated.NewExpiresInSeconds())
                    .ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage RedirectTo(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.TryAddWithoutValidation(PsnSession.LocationHeaderName, location);
        return response;
    }

    private static HttpResponseMessage TokenResponse(string accessToken) => Json(
        HttpStatusCode.OK,
        TokenEndpointJson(accessToken));

    private static string TokenEndpointJson(string accessToken) =>
        JsonSerializer.Serialize(new PsnTokenEndpointResponse
        {
            AccessToken = accessToken,
            RefreshToken = Generated.NewRefreshToken(),
            ExpiresIn = Generated.NewExpiresInSeconds(),
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json),
        };

    private static string ConceptsPathFor(string titleId) =>
        $"{new Uri(PsnCatalogClient.GameTitlesUri).AbsolutePath}/{titleId}/{PsnCatalogClient.ConceptsPathSegment}";

    private static string QueryPair(string key, string value) => $"{key}={value}";

    private static string RedirectCarryingAuthorizationCode(string authorizationCode) =>
        $"{PsnSession.RedirectUri}?{PsnSession.AuthorizationCodeQueryKey}={authorizationCode}";

    private static IReadOnlyList<string> NewTitleIds() =>
        [Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix), Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix)];
}
