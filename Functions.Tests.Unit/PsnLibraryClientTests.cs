namespace Functions.Tests.Unit;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PsnLibraryClientTests
{
    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task EntitlementsAsync_ReturnsNothing_WhenTheLibraryIsEmpty()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(Page(totalResults: 0)));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(entitlements);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EntitlementsAsync_ReturnsNothing_WhenPsnOmitsTheEntitlementsKeyEntirely()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json("""{"totalResults": 0}"""));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(entitlements);
    }

    [Fact]
    public async Task EntitlementsAsync_PagesUntilPsnReturnsAShortPage()
    {
        // Arrange
        var secondPageCount = Random.Shared.Next(1, PsnLibraryClient.PageSize);
        var total = PsnLibraryClient.PageSize + secondPageCount;
        var handler = StubHttpMessageHandler.Sequence(
            Json(Page(total, Entries(count: PsnLibraryClient.PageSize, firstIndex: 0))),
            Json(Page(total, Entries(count: secondPageCount, firstIndex: PsnLibraryClient.PageSize))));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var lastIndex = PsnLibraryClient.PageSize + secondPageCount - 1;
        Assert.Equal(total, entitlements.Count);
        Assert.Equal("ent-0", entitlements[0].EntitlementId);
        Assert.Equal($"ent-{lastIndex}", entitlements[lastIndex].EntitlementId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("offset=0", handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains(
            $"offset={PsnLibraryClient.PageSize}",
            handler.Requests[1].RequestUri?.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntitlementsAsync_StopsPaging_WhenTheAccumulatedOffsetReachesTotalResults()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(Json(Page(totalResults: PsnLibraryClient.PageSize, Entries(count: PsnLibraryClient.PageSize, firstIndex: 0))));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PsnLibraryClient.PageSize, entitlements.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EntitlementsAsync_KeepsPagingUntilAShortPage_WhenPsnOmitsTotalResults()
    {
        // Arrange
        var secondPageCount = Random.Shared.Next(1, PsnLibraryClient.PageSize);
        var handler = StubHttpMessageHandler.Sequence(
            Json(Page(totalResults: null, Entries(count: PsnLibraryClient.PageSize, firstIndex: 0))),
            Json(Page(totalResults: null, Entries(count: secondPageCount, firstIndex: PsnLibraryClient.PageSize))));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PsnLibraryClient.PageSize + secondPageCount, entitlements.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EntitlementsAsync_ReturnsAnEntitlementOnce_WhenAShiftingPageWindowServesItTwice()
    {
        // Arrange
        var firstPage = Entries(count: PsnLibraryClient.PageSize, firstIndex: 0);
        var repeatedEntitlementId = firstPage[^1].Id;
        var secondPageOnlyEntitlementId = NewToken("ent");
        var inflatedTotalPsnReports = PsnLibraryClient.PageSize + 2;
        var handler = StubHttpMessageHandler.Sequence(
            Json(Page(inflatedTotalPsnReports, firstPage)),
            Json(Page(
                inflatedTotalPsnReports,
                new PsnEntitlementPayload { Id = repeatedEntitlementId },
                new PsnEntitlementPayload { Id = secondPageOnlyEntitlementId })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PsnLibraryClient.PageSize + 1, entitlements.Count);
        Assert.Equal(
            1,
            entitlements.Count(entitlement =>
                string.Equals(entitlement.EntitlementId, repeatedEntitlementId, StringComparison.Ordinal)));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EntitlementsAsync_KeepsEveryIdLessEntitlement_SoTheyAreCountedAndSkippedIndividually()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(Page(
            totalResults: 2,
            new PsnEntitlementPayload { TitleMeta = new PsnTitleMeta { Name = NewToken("title") } },
            new PsnEntitlementPayload { TitleMeta = new PsnTitleMeta { Name = NewToken("title") } })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, entitlements.Count);
        Assert.All(entitlements, entitlement => Assert.Null(entitlement.EntitlementId));
    }

    [Fact]
    public async Task EntitlementsAsync_StopsAtTheRequestedLimit()
    {
        // Arrange
        var requestedLimit = Random.Shared.Next(1, PsnLibraryClient.PageSize);
        var totalAvailable = requestedLimit + Random.Shared.Next(1, 10_000);
        var handler = StubHttpMessageHandler.Sequence(Json(Page(totalResults: totalAvailable, Entries(count: requestedLimit, firstIndex: 0))));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, requestedLimit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(requestedLimit, entitlements.Count);
        var sentRequest = Assert.Single(handler.Requests);
        Assert.Contains($"limit={requestedLimit}", sentRequest.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntitlementsAsync_RequestsEveryEntitlementTypeAndMetadataBlockPsnExposes()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Json(Page(totalResults: 0)));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        var requestedUri = Assert.IsType<Uri>(request.RequestUri);
        var query = Uri.UnescapeDataString(requestedUri.Query);
        Assert.Equal(new Uri(PsnLibraryClient.EntitlementsUrl).AbsolutePath, requestedUri.AbsolutePath);
        Assert.Contains($"entitlementType={PsnLibraryClient.EntitlementTypes}", query, StringComparison.Ordinal);
        Assert.Contains($"fields={PsnLibraryClient.RequestedFields}", query, StringComparison.Ordinal);
        Assert.Contains($"limit={PsnLibraryClient.PageSize}", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntitlementsAsync_KeepsAllThreeArtworkUrlsPsnReturns()
    {
        // Arrange
        var titleImageUrl = NewImageUrl();
        var gameIconUrl = NewImageUrl();
        var conceptIconUrl = NewImageUrl();
        var handler = StubHttpMessageHandler.Returns(Json(Page(
            totalResults: 1,
            new PsnEntitlementPayload
            {
                Id = NewToken("ent"),
                TitleMeta = new PsnTitleMeta { ImageUrl = titleImageUrl },
                GameMeta = new PsnGameMeta { IconUrl = gameIconUrl },
                ConceptMeta = new PsnConceptMeta { IconUrl = conceptIconUrl },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var entitlement = Assert.Single(entitlements);
        Assert.Equal(titleImageUrl, entitlement.TitleImageUrl);
        Assert.Equal(gameIconUrl, entitlement.GameIconUrl);
        Assert.Equal(conceptIconUrl, entitlement.ConceptIconUrl);
        Assert.Equal(titleImageUrl, entitlement.ImageUrl);
    }

    [Fact]
    public async Task EntitlementsAsync_FallsBackToTheGameIcon_WhenTheTitleHasNoImageUrl()
    {
        // Arrange
        var gameIconUrl = NewImageUrl();
        var handler = StubHttpMessageHandler.Returns(Json(Page(
            totalResults: 1,
            new PsnEntitlementPayload
            {
                Id = NewToken("ent"),
                TitleMeta = new PsnTitleMeta { Name = NewToken("title") },
                GameMeta = new PsnGameMeta { IconUrl = gameIconUrl },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var entitlement = Assert.Single(entitlements);
        Assert.Null(entitlement.TitleImageUrl);
        Assert.Equal(gameIconUrl, entitlement.ImageUrl);
    }

    [Fact]
    public async Task EntitlementsAsync_MapsEveryColumnIngestionPersists()
    {
        // Arrange
        var entitlementId = NewToken("ent");
        var productId = NewToken("product");
        var skuId = NewToken("sku");
        var titleId = NewToken("title");
        var conceptId = NewToken("concept");
        var activeDate = NewActiveDate();
        var titleMetaName = NewToken("title-name");
        var gameMetaName = NewToken("game-name");
        var conceptMetaName = NewToken("concept-name");
        var handler = StubHttpMessageHandler.Returns(Json(Page(
            totalResults: 1,
            new PsnEntitlementPayload
            {
                Id = entitlementId,
                ProductId = productId,
                SkuId = skuId,
                ActiveFlag = true,
                ActiveDate = activeDate,
                IsGame = true,
                TitleMeta = new PsnTitleMeta { TitleId = titleId, Name = titleMetaName },
                GameMeta = new PsnGameMeta
                {
                    Name = gameMetaName,
                    PackageType = NewToken("pkg"),
                    Type = NewToken("type"),
                },
                ConceptMeta = new PsnConceptMeta { ConceptId = conceptId, Name = conceptMetaName },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var entitlement = Assert.Single(entitlements);
        Assert.Equal(entitlementId, entitlement.EntitlementId);
        Assert.Equal(productId, entitlement.ProductId);
        Assert.Equal(skuId, entitlement.SkuId);
        Assert.Equal(titleId, entitlement.TitleId);
        Assert.Equal(conceptId, entitlement.ConceptId);
        Assert.Equal(activeDate.ToUniversalTime(), entitlement.ActiveDate);
        Assert.True(entitlement.Active);
        Assert.True(entitlement.IsGame);
        Assert.Equal(gameMetaName, entitlement.GameMetaName);
        Assert.Equal(titleMetaName, entitlement.TitleMetaName);
        Assert.Equal(conceptMetaName, entitlement.ConceptMetaName);
    }

    [Fact]
    public async Task EntitlementsAsync_ReadsPackageTypeFromGameMetaPackageTypeNotGameMetaType()
    {
        // Arrange
        var packageType = NewToken("pkg");
        var gameType = NewToken("type");
        var handler = StubHttpMessageHandler.Returns(Json(Page(
            totalResults: 1,
            new PsnEntitlementPayload
            {
                Id = NewToken("ent"),
                GameMeta = new PsnGameMeta { PackageType = packageType, Type = gameType },
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var entitlement = Assert.Single(entitlements);
        Assert.Equal(packageType, entitlement.PackageType);
        Assert.Equal(gameType, entitlement.GameType);
    }

    [Fact]
    public async Task EntitlementsAsync_CollectsPlatformIdsAndSkipsAttributesWithoutOne()
    {
        // Arrange
        var firstPlatformId = NewToken("platform");
        var secondPlatformId = NewToken("platform");
        var handler = StubHttpMessageHandler.Returns(Json(Page(
            totalResults: 1,
            new PsnEntitlementPayload
            {
                Id = NewToken("ent"),
                EntitlementAttributes =
                [
                    new PsnEntitlementAttribute { PlatformId = firstPlatformId },
                    new PsnEntitlementAttribute { PlatformId = string.Empty },
                    new PsnEntitlementAttribute { PlatformId = "   " },
                    new PsnEntitlementAttribute(),
                    new PsnEntitlementAttribute { PlatformId = secondPlatformId },
                ],
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var entitlement = Assert.Single(entitlements);
        Assert.Equal([firstPlatformId, secondPlatformId], entitlement.PlatformIds);
    }

    [Fact]
    public async Task EntitlementsAsync_HasNoPlatformIds_WhenPsnSendsNoEntitlementAttributes()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(
            Json(Page(totalResults: 1, new PsnEntitlementPayload { Id = NewToken("ent") })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(Assert.Single(entitlements).PlatformIds);
    }

    [Fact]
    public async Task EntitlementsAsync_NormalisesActiveDateToUtc_WhenPsnSendsAnOffsetTimestamp()
    {
        // Arrange
        var nonUtcActiveDate = NewNonUtcActiveDate();
        var handler = StubHttpMessageHandler.Returns(Json(Page(
            totalResults: 1,
            new PsnEntitlementPayload
            {
                Id = NewToken("ent"),
                ActiveDate = nonUtcActiveDate,
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var activeDate = Assert.Single(entitlements).ActiveDate;
        Assert.Equal(nonUtcActiveDate.ToUniversalTime(), activeDate);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(activeDate).Offset);
    }

    [Fact]
    public async Task EntitlementsAsync_KeepsPsnsVerbatimEntryAsRawSoAMappingBugCannotLoseAField()
    {
        // Arrange
        const string body = """
            {"totalResults": 1, "entitlements": [
                {"id": "ent-1", "rewardMeta": {"retentionPolicy": "KEEP"}, "neverMapped": 42}
            ]}
            """;
        var handler = StubHttpMessageHandler.Returns(Json(body));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var raw = Assert.Single(entitlements).Raw;
        Assert.Contains("\"neverMapped\": 42", raw, StringComparison.Ordinal);
        Assert.Contains("\"retentionPolicy\": \"KEEP\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntitlementsAsync_ThrowsPsnAuthException_WhenPsnRejectsTheTokenAndNothingCanReauthenticate()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<PsnAuthException>(exception);
    }

    [Fact]
    public async Task EntitlementsAsync_ReauthenticatesAndRetriesOnce_WhenTheCachedTokenIsRejectedAndAnNpssoIsAvailable()
    {
        // Arrange
        var recoveredAccessToken = NewToken("access");
        var recoveredEntitlementId = NewToken("ent");
        var handler = StubHttpMessageHandler.Sequence(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            RedirectTo("com.scee.psxandroid.scecompcall://redirect?code=one-time-code"),
            Json(TokenEndpointJson(recoveredAccessToken)),
            Json(Page(totalResults: 1, new PsnEntitlementPayload { Id = recoveredEntitlementId })));
        var session = await PsnSession.RestoreAsync(
            NewToken("npsso"),
            SeededStore(),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(recoveredEntitlementId, Assert.Single(entitlements).EntitlementId);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal($"Bearer {recoveredAccessToken}", handler.Requests[3].Headers.Authorization?.ToString());
    }

    private static string Page(int? totalResults, params PsnEntitlementPayload[] entitlements) =>
        JsonSerializer.Serialize(
            new PsnEntitlementsResponse
            {
                TotalResults = totalResults,
                Entitlements = [.. entitlements.Select(entitlement => JsonSerializer.SerializeToElement(entitlement, PsnWireFormat))],
            },
            PsnWireFormat);

    private static PsnEntitlementPayload[] Entries(int count, int firstIndex) =>
        [.. Enumerable.Range(firstIndex, count).Select(index => new PsnEntitlementPayload { Id = $"ent-{index}" })];

    private static async Task<PsnSession> ReadySessionAsync(StubHttpMessageHandler handler) =>
        await PsnSession.RestoreAsync(
            null,
            SeededStore(),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

    private static InMemoryPsnTokenStore SeededStore()
    {
        var expiresInSeconds = Random.Shared.Next(1, 90_000);
        var store = new InMemoryPsnTokenStore();
        store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = NewToken("cached-access"),
                ExpiresIn = expiresInSeconds,
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

    private static string TokenEndpointJson(string accessToken)
    {
        var expiresInSeconds = Random.Shared.Next(1, 90_000);
        return JsonSerializer.Serialize(new PsnTokenEndpointResponse
        {
            AccessToken = accessToken,
            RefreshToken = NewToken("refresh"),
            ExpiresIn = expiresInSeconds,
        });
    }

    private static string NewToken(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string NewImageUrl() => $"https://example.com/{Guid.NewGuid():N}.png";

    private static DateTimeOffset NewActiveDate() =>
        DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 3_650)).AddSeconds(-Random.Shared.Next(0, 86_400));

    private static DateTimeOffset NewNonUtcActiveDate()
    {
        var year = 2000 + Random.Shared.Next(1, 26);
        var month = Random.Shared.Next(1, 13);
        var day = Random.Shared.Next(1, 28);
        var hour = Random.Shared.Next(0, 24);
        var minute = Random.Shared.Next(0, 60);
        var second = Random.Shared.Next(0, 60);
        var offset = TimeSpan.FromHours(Random.Shared.Next(1, 13));
        return new DateTimeOffset(year, month, day, hour, minute, second, offset);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
