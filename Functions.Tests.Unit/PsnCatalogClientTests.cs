namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PsnCatalogClientTests
{
    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task TitleConceptAsync_ParsesTheConceptPayload()
    {
        // Arrange
        var titleId = NewTitleId();
        var conceptNumericId = NewConceptNumericId();
        var name = TestValues.NewGameName();
        var publisherName = NewPublisherName();
        var releaseDate = NewReleaseDate();
        var minimumAge = Random.Shared.Next(0, 21);
        var contentRatingSymbolicName = NewContentRatingSymbolicName();
        var ratingAuthority = NewRatingAuthority();
        var starRating = NewStarRating();
        var genres = NewGenres();
        var titleIds = NewTitleIds();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = conceptNumericId,
                Name = name,
                Type = NewConceptType(),
                PublisherName = publisherName,
                MinimumAge = minimumAge,
                ReleaseDate = new PsnReleaseDate
                {
                    Date = releaseDate,
                    Type = NewReleaseDateType(),
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
        var conceptNumericId = NewConceptNumericId();
        var contentRatingAuthority = NewRatingAuthority();
        var contentRatingDescription = NewContentRatingDescription();
        var contentRatingSymbolicName = NewContentRatingSymbolicName();
        var body = $$$"""
            [{"id": {{{conceptNumericId}}}, "contentRating": {
                "authority": "{{{contentRatingAuthority}}}", "description": "{{{contentRatingDescription}}}", "name": "{{{contentRatingSymbolicName}}}"
            }}]
            """;
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, body));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
                Id = NewConceptNumericId(),
                Name = name,
                ReleaseDate = new PsnReleaseDate { Type = "COMING_SOON" },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(concept.ReleaseDate);
        Assert.Equal(name, concept.Name);
    }

    [Fact]
    public async Task TitleConceptAsync_NormalisesTheReleaseDateToUtc_WhenPsnSendsAnOffsetTimestamp()
    {
        // Arrange
        var releaseDateUtc = NewReleaseDate();
        var sourceOffset = TimeSpan.FromHours(Random.Shared.Next(1, 13));
        var releaseDateWithOffset = releaseDateUtc.ToOffset(sourceOffset);
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = NewConceptNumericId(),
                ReleaseDate = new PsnReleaseDate { Date = releaseDateWithOffset },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(releaseDateUtc, concept.ReleaseDate);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(concept.ReleaseDate).Offset);
    }

    [Fact]
    public async Task TitleConceptAsync_RequestsTheAgeCountryAndLanguageQueryParametersPsnRequires()
    {
        // Arrange
        var titleId = NewTitleId();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        await client.TitleConceptAsync(session, titleId, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/api/catalog/v2/titles/{titleId}/concepts", request.RequestUri?.AbsolutePath);
        Assert.Contains($"age={PsnCatalogClient.AgeQueryValue}", request.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains($"country={PsnCatalogClient.CountryQueryValue}", request.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains($"language={PsnCatalogClient.LanguageQueryValue}", request.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenNoConceptsAreReturned_ReturnsAnEmptyTitleConcept()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts()));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
        var otherImageType = NewImageType();
        var preferredUrl = NewImageUrl();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        Image(otherImageType, NewImageUrl()),
                        Image(PsnCatalogClient.CoverImagePreference[0], preferredUrl),
                        Image(PsnCatalogClient.CoverImagePreference[1], NewImageUrl()),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(preferredUrl, concept.CoverImageUrl);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenOneImageTypeAppearsTwice_TheLastOneWins()
    {
        // Arrange
        var imageType = PsnCatalogClient.CoverImagePreference[0];
        var lastUrl = NewImageUrl();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        Image(imageType, NewImageUrl()),
                        Image(imageType, lastUrl),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(lastUrl, concept.CoverImageUrl);
    }

    [Fact]
    public async Task TitleConceptAsync_WhenNoPreferredRoleIsPresent_FallsBackToTheFirstImageWithAUrl()
    {
        // Arrange
        var firstUrl = NewImageUrl();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = NewConceptNumericId(),
                Media = new PsnConceptMedia
                {
                    Images =
                    [
                        new PsnConceptImage { Type = NewImageType() },
                        Image(NewImageType(), firstUrl),
                        Image(NewImageType(), NewImageUrl()),
                    ],
                },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
                Id = NewConceptNumericId(),
                CompatibilityNotices =
                [
                    Notice(NewNoticeType(), true),
                    Notice(NewNoticeType(), NewNonNumericToken()),
                ],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
                Id = NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfPlayersNoticeType, 1)],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.False(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_MultiplayerIsTrueWhenAnOnlineNetworkNoticeExceedsOne()
    {
        // Arrange
        var networkPlayerCount = NewMultiplayerPlayerCount();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = NewConceptNumericId(),
                CompatibilityNotices =
                [
                    Notice(PsnCatalogClient.NoOfPlayersNoticeType, 1),
                    Notice(PsnCatalogClient.NoOfNetworkPlayersNoticeType, networkPlayerCount),
                    Notice(PsnCatalogClient.NoOfNetworkPlayersPsPlusNoticeType, networkPlayerCount),
                ],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(concept.Multiplayer);
    }

    [Fact]
    public async Task TitleConceptAsync_ReadsAPlayerCountPsnStringified()
    {
        // Arrange
        var networkPlayerCount = NewMultiplayerPlayerCount().ToString(CultureInfo.InvariantCulture);
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, Concepts(
            new PsnConceptPayload
            {
                Id = NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfNetworkPlayersNoticeType, networkPlayerCount)],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
                Id = NewConceptNumericId(),
                CompatibilityNotices = [Notice(PsnCatalogClient.NoOfPlayersNoticeType, NewNonNumericToken())],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

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
        var handler = StubHttpMessageHandler.Sequence(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            RedirectTo($"https://example.com/redirect?code={authorizationCode}"),
            TokenResponse(refreshedAccessToken),
            Json(HttpStatusCode.OK, Concepts(new PsnConceptPayload { Id = NewConceptNumericId(), Name = recoveredName })));
        var session = await PsnSession.RestoreAsync(
            npsso,
            SeededStore(cachedAccessToken),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        var client = new PsnCatalogClient();

        // Act
        var concept = await client.TitleConceptAsync(session, NewTitleId(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(recoveredName, concept.Name);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal($"{PsnSession.BearerScheme} {cachedAccessToken}", handler.Requests[0].Headers.Authorization?.ToString());
        Assert.Equal($"{PsnSession.BearerScheme} {refreshedAccessToken}", handler.Requests[3].Headers.Authorization?.ToString());
    }

    private static string Concepts(params PsnConceptPayload[] concepts) =>
        JsonSerializer.Serialize(concepts, PsnWireFormat);

    private static PsnConceptImage Image(string type, string url) => new() { Type = type, Url = url };

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
                ExpiresIn = NewExpiresInSeconds(),
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage RedirectTo(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.TryAddWithoutValidation("Location", location);
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
            ExpiresIn = NewExpiresInSeconds(),
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string NewTitleId() => TestValues.NewTitleId();

    private static int NewConceptNumericId() => Random.Shared.Next(1, 100_000_000);

    private static string NewPublisherName() => $"Publisher {Guid.NewGuid():N}";

    private static string NewConceptType() => $"type-{Guid.NewGuid():N}";

    private static string NewReleaseDateType() => $"release-type-{Guid.NewGuid():N}";

    private static DateTimeOffset NewReleaseDate() =>
        new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-Random.Shared.Next(1, 3650));

    private static string NewContentRatingDescription() => $"Rated {Guid.NewGuid():N}";

    private static string NewContentRatingSymbolicName() => $"RATED_{Guid.NewGuid():N}";

    private static string NewRatingAuthority() => TestValues.NewRatingAuthority();

    private static double NewStarRating() => TestValues.NewStarRating();

    private static IReadOnlyList<string> NewGenres() => [$"genre-{Guid.NewGuid():N}", $"genre-{Guid.NewGuid():N}"];

    private static IReadOnlyList<string> NewTitleIds() => [NewTitleId(), NewTitleId()];

    private static string NewImageType() => $"image-type-{Guid.NewGuid():N}";

    private static string NewImageUrl() => $"https://example.com/{Guid.NewGuid():N}.jpg";

    private static string NewNoticeType() => $"notice-type-{Guid.NewGuid():N}";

    private static int NewMultiplayerPlayerCount() => Random.Shared.Next(2, 100);

    private static string NewNonNumericToken() => $"count-{Guid.NewGuid():N}";

    private static int NewExpiresInSeconds() => Random.Shared.Next(60, 86_400);
}
