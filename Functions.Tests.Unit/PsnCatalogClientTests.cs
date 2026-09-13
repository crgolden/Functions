namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PsnCatalogClientTests
{
    private const int SinglePlayerOnly = 1;

    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task TitleConceptAsync_ParsesTheConceptPayload()
    {
        // Arrange
        var titleId = TestValues.NewTitleId();
        var conceptNumericId = TestValues.NewConceptNumericId();
        var name = TestValues.NewGameName();
        var publisherName = TestValues.NewPublisher();
        var releaseDate = TestValues.NewReleaseTimestamp();
        var minimumAge = TestValues.NewMinimumAge();
        var contentRatingSymbolicName = TestValues.NewContentRating();
        var ratingAuthority = TestValues.NewRatingAuthority();
        var starRating = TestValues.NewStarRating();
        var genres = TestValues.NewGenreList(Random.Shared.Next(2, 5));
        var titleIds = NewTitleIds();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = conceptNumericId,
                Name = name,
                Type = TestValues.NewConceptType(),
                PublisherName = publisherName,
                MinimumAge = minimumAge,
                ReleaseDate = new PsnReleaseDate
                {
                    Date = releaseDate,
                    Type = TestValues.NewReleaseDateType(),
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
        var conceptNumericId = TestValues.NewConceptNumericId();
        var contentRatingAuthority = TestValues.NewRatingAuthority();
        var contentRatingDescription = TestValues.NewContentRatingDescription();
        var contentRatingSymbolicName = TestValues.NewContentRating();
        var body = $$$"""
            [{"id": {{{conceptNumericId}}}, "contentRating": {
                "authority": "{{{contentRatingAuthority}}}", "description": "{{{contentRatingDescription}}}", "name": "{{{contentRatingSymbolicName}}}"
            }}]
            """;
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, body));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(contentRatingSymbolicName, concept.ContentRating);
        Assert.NotEqual(contentRatingDescription, concept.ContentRating);
    }

    [Fact]
    public async Task TitleConceptAsync_HasNoReleaseDate_WhenPsnPublishesOnlyAComingSoonLabel()
    {
        // Arrange
        var name = TestValues.NewGameName();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = TestValues.NewConceptNumericId(),
                Name = name,
                ReleaseDate = new PsnReleaseDate { Type = TestValues.NewReleaseDateType() },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(concept.ReleaseDate);
        Assert.Equal(name, concept.Name);
    }

    [Fact]
    public async Task TitleConceptAsync_NormalisesTheReleaseDateToUtc_WhenPsnSendsAnOffsetTimestamp()
    {
        // Arrange
        var releaseDateUtc = TestValues.NewReleaseTimestamp();
        var sourceOffset = TestValues.NewNonZeroUtcOffset();
        var releaseDateWithOffset = releaseDateUtc.ToOffset(sourceOffset);
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = TestValues.NewConceptNumericId(),
                ReleaseDate = new PsnReleaseDate { Date = releaseDateWithOffset },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(releaseDateUtc, concept.ReleaseDate);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(concept.ReleaseDate).Offset);
    }

    [Fact]
    public async Task TitleConceptAsync_RequestsTheAgeCountryAndLanguageQueryParametersPsnRequires()
    {
        // Arrange
        var titleId = TestValues.NewTitleId();
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
    public async Task TitleConceptAsync_WhenNoConceptsAreReturned_ReturnsAnEmptyTitleConcept()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
        var otherImageType = TestValues.NewImageType();
        var preferredUrl = TestValues.NewCoverImageUri();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = TestValues.NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        Image(otherImageType, TestValues.NewCoverImageUri()),
                        Image(PsnCatalogClient.CoverImagePreference[0], preferredUrl),
                        Image(PsnCatalogClient.CoverImagePreference[1], TestValues.NewCoverImageUri()),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(preferredUrl, concept.CoverImageUrl);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenOneImageTypeAppearsTwice_TheLastOneWins()
    {
        // Arrange
        var imageType = PsnCatalogClient.CoverImagePreference[0];
        var lastUrl = TestValues.NewCoverImageUri();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = TestValues.NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        Image(imageType, TestValues.NewCoverImageUri()),
                        Image(imageType, lastUrl),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(lastUrl, concept.CoverImageUrl);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenNoPreferredRoleIsPresent_FallsBackToTheFirstImageWithAUrl()
    {
        // Arrange
        var firstUrl = TestValues.NewCoverImageUri();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = TestValues.NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        new PsnConceptImage { Type = TestValues.NewImageType() },
                        Image(TestValues.NewImageType(), firstUrl),
                        Image(TestValues.NewImageType(), TestValues.NewCoverImageUri()),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
                Id = TestValues.NewConceptNumericId(),
                CompatibilityNotices =
                [
                    Notice(TestValues.NewCompatibilityNoticeType(), true),
                    Notice(TestValues.NewCompatibilityNoticeType(), TestValues.NewNonNumericToken()),
                ],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
                Id = TestValues.NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfPlayersNoticeType, SinglePlayerOnly)],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.False(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_MultiplayerIsTrueWhenAnOnlineNetworkNoticeExceedsOne()
    {
        // Arrange
        var networkPlayerCount = TestValues.NewMultiplayerPlayerCount();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = TestValues.NewConceptNumericId(),
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
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_ReadsAPlayerCountPsnStringified()
    {
        // Arrange
        var networkPlayerCount = TestValues.NewMultiplayerPlayerCount().ToString(CultureInfo.InvariantCulture);
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = TestValues.NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfNetworkPlayersNoticeType, networkPlayerCount)],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
                Id = TestValues.NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfPlayersNoticeType, TestValues.NewNonNumericToken())],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenTheCachedTokenIsRejectedAndAnNpssoIsAvailable_ReauthenticatesAndRetriesOnce()
    {
        // Arrange
        var npsso = TestValues.NewNpsso();
        var cachedAccessToken = TestValues.NewAccessToken();
        var authorizationCode = TestValues.NewAuthorizationCode();
        var refreshedAccessToken = TestValues.NewAccessToken();
        var recoveredName = TestValues.NewGameName();
        var exchange = new[]
        {
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            RedirectTo(RedirectCarryingAuthorizationCode(authorizationCode)),
            TokenResponse(refreshedAccessToken),
            Json(HttpStatusCode.OK, Concepts(new PsnConceptPayload { Id = TestValues.NewConceptNumericId(), Name = recoveredName })),
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
        var concept = await client.TitleConceptAsync(session, TestValues.NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
            SeededStore(TestValues.NewAccessToken()),
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
                ExpiresIn = TestValues.NewExpiresInSeconds(),
                AccessTokenExpiresAt = DateTimeOffset.UtcNow
                    .AddSeconds(TestValues.NewExpiresInSeconds())
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
            RefreshToken = TestValues.NewRefreshToken(),
            ExpiresIn = TestValues.NewExpiresInSeconds(),
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
        [TestValues.NewTitleId(), TestValues.NewTitleId()];
}
