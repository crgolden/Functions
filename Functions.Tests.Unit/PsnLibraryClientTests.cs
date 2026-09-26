namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Functions.Curator.Psn;
using Functions.Tests.Unit.TestSupport;
using static Functions.Tests.Unit.PsnLibraryClientFixtureConstants;

[Trait("Category", "Unit")]
public sealed class PsnLibraryClientTests
{
    private static readonly string EntitlementIdPrefix = Generated.NewEntitlementIdPrefix();

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
        var handler = StubHttpMessageHandler.Returns(Json(PageWithoutTheEntitlementsKey()));
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
        var pages = new[]
        {
            Json(Page(total, Entries(count: PsnLibraryClient.PageSize, firstIndex: 0))),
            Json(Page(total, Entries(count: secondPageCount, firstIndex: PsnLibraryClient.PageSize))),
        };
        var handler = StubHttpMessageHandler.Sequence(pages);
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var lastIndex = PsnLibraryClient.PageSize + secondPageCount - 1;
        Assert.Equal(total, entitlements.Count);
        Assert.Equal(EntitlementIdAt(0), entitlements[0].EntitlementId);
        Assert.Equal(EntitlementIdAt(lastIndex), entitlements[lastIndex].EntitlementId);
        Assert.Equal(pages.Length, handler.Requests.Count);
        Assert.Contains(
            QueryPair(PsnLibraryClient.OffsetQueryKey, FirstPageOffset),
            handler.Requests[0].RequestUri?.Query,
            StringComparison.Ordinal);
        Assert.Contains(
            QueryPair(PsnLibraryClient.OffsetQueryKey, PsnLibraryClient.PageSize),
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
        var pages = new[]
        {
            Json(Page(totalResults: null, Entries(count: PsnLibraryClient.PageSize, firstIndex: 0))),
            Json(Page(totalResults: null, Entries(count: secondPageCount, firstIndex: PsnLibraryClient.PageSize))),
        };
        var handler = StubHttpMessageHandler.Sequence(pages);
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PsnLibraryClient.PageSize + secondPageCount, entitlements.Count);
        Assert.Equal(pages.Length, handler.Requests.Count);
    }

    [Fact]
    public async Task EntitlementsAsync_ReturnsAnEntitlementOnce_WhenAShiftingPageWindowServesItTwice()
    {
        // Arrange
        var firstPage = Entries(count: PsnLibraryClient.PageSize, firstIndex: 0);
        var repeatedEntitlementId = firstPage[^1].Id;
        var secondPageOnlyEntitlementId = Generated.NewEntitlementId();
        var secondPage = new[]
        {
            new PsnEntitlementPayload { Id = repeatedEntitlementId },
            new PsnEntitlementPayload { Id = secondPageOnlyEntitlementId },
        };
        var inflatedTotalPsnReports = firstPage.Length + secondPage.Length;
        var pages = new[]
        {
            Json(Page(inflatedTotalPsnReports, firstPage)),
            Json(Page(inflatedTotalPsnReports, secondPage)),
        };
        var handler = StubHttpMessageHandler.Sequence(pages);
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(firstPage.Length + 1, entitlements.Count);
        Assert.Single(
            entitlements,
            entitlement => string.Equals(entitlement.EntitlementId, repeatedEntitlementId, StringComparison.Ordinal));
        Assert.Equal(pages.Length, handler.Requests.Count);
    }

    [Fact]
    public async Task EntitlementsAsync_KeepsEveryIdLessEntitlement_SoTheyAreCountedAndSkippedIndividually()
    {
        // Arrange
        var idLessEntitlements = new[]
        {
            new PsnEntitlementPayload { TitleMeta = new PsnTitleMeta { Name = Generated.NewGameTitle() } },
            new PsnEntitlementPayload { TitleMeta = new PsnTitleMeta { Name = Generated.NewGameTitle() } },
        };
        var handler = StubHttpMessageHandler.Returns(
            Json(Page(idLessEntitlements.Length, idLessEntitlements)));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(idLessEntitlements.Length, entitlements.Count);
        Assert.All(entitlements, entitlement => Assert.Null(entitlement.EntitlementId));
    }

    [Fact]
    public async Task EntitlementsAsync_StopsAtTheRequestedLimit()
    {
        // Arrange
        var requestedLimit = Random.Shared.Next(1, PsnLibraryClient.PageSize);
        var totalAvailable = requestedLimit + Generated.NewEntitlementsBeyondTheLimit();
        var handler = StubHttpMessageHandler.Sequence(Json(Page(totalResults: totalAvailable, Entries(count: requestedLimit, firstIndex: 0))));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, requestedLimit, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(requestedLimit, entitlements.Count);
        var sentRequest = Assert.Single(handler.Requests);
        Assert.Contains(
            QueryPair(PsnLibraryClient.LimitQueryKey, requestedLimit),
            sentRequest.RequestUri?.Query,
            StringComparison.Ordinal);
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
        Assert.Contains(
            QueryPair(PsnLibraryClient.EntitlementTypeQueryKey, PsnLibraryClient.EntitlementTypes),
            query,
            StringComparison.Ordinal);
        Assert.Contains(
            QueryPair(PsnLibraryClient.FieldsQueryKey, PsnLibraryClient.RequestedFields),
            query,
            StringComparison.Ordinal);
        Assert.Contains(
            QueryPair(PsnLibraryClient.LimitQueryKey, PsnLibraryClient.PageSize),
            query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntitlementsAsync_KeepsAllThreeArtworkUrlsPsnReturns()
    {
        // Arrange
        var titleImageUrl = Generated.NewCoverImageUri();
        var gameIconUrl = Generated.NewCoverImageUri();
        var conceptIconUrl = Generated.NewCoverImageUri();
        var handler = StubHttpMessageHandler.Returns(Json(SinglePage(
            new PsnEntitlementPayload
            {
                Id = Generated.NewEntitlementId(),
                TitleMeta = new PsnTitleMeta { ImageUrl = titleImageUrl.OriginalString },
                GameMeta = new PsnGameMeta { IconUrl = gameIconUrl.OriginalString },
                ConceptMeta = new PsnConceptMeta { IconUrl = conceptIconUrl.OriginalString },
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
        var gameIconUrl = Generated.NewCoverImageUri();
        var handler = StubHttpMessageHandler.Returns(Json(SinglePage(
            new PsnEntitlementPayload
            {
                Id = Generated.NewEntitlementId(),
                TitleMeta = new PsnTitleMeta { Name = Generated.NewGameTitle() },
                GameMeta = new PsnGameMeta { IconUrl = gameIconUrl.OriginalString },
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
        var entitlementId = Generated.NewEntitlementId();
        var productId = Generated.NewProductId();
        var skuId = Generated.NewSkuId();
        var titleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix);
        var conceptId = Generated.NewConceptId();
        var activeDate = Generated.NewUtcTimestamp();
        var titleMetaName = Generated.NewGameTitle();
        var gameMetaName = Generated.NewGameTitle();
        var conceptMetaName = Generated.NewGameTitle();
        var handler = StubHttpMessageHandler.Returns(Json(SinglePage(
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
                    PackageType = Generated.NewPackageType(),
                    Type = Generated.NewGameType(),
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
        var packageType = Generated.NewPackageType();
        var gameType = Generated.NewGameType();
        var handler = StubHttpMessageHandler.Returns(Json(SinglePage(
            new PsnEntitlementPayload
            {
                Id = Generated.NewEntitlementId(),
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
        var firstPlatformId = Generated.NewPlatformId();
        var secondPlatformId = Generated.NewPlatformId();
        var handler = StubHttpMessageHandler.Returns(Json(SinglePage(
            new PsnEntitlementPayload
            {
                Id = Generated.NewEntitlementId(),
                EntitlementAttributes =
                [
                    new PsnEntitlementAttribute { PlatformId = firstPlatformId },
                    new PsnEntitlementAttribute { PlatformId = Generated.NewBlankRun() },
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
            Json(SinglePage(new PsnEntitlementPayload { Id = Generated.NewEntitlementId() })));
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
        var nonUtcActiveDate = Generated.NewTimestampWithNonZeroOffset();
        var handler = StubHttpMessageHandler.Returns(Json(SinglePage(
            new PsnEntitlementPayload
            {
                Id = Generated.NewEntitlementId(),
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
        var neverMappedName = Generated.NewJsonPropertyName();
        var neverMappedValue = Random.Shared.Next(1, 1_000);
        var neverMappedBlockName = Generated.NewJsonPropertyName();
        var neverMappedNestedName = Generated.NewJsonPropertyName();
        var neverMappedNestedValue = Generated.LowercaseToken(6);
        var handler = StubHttpMessageHandler.Returns(Json(PageCarrying(
            new JsonObject
            {
                [PsnEntitlementPayload.IdPropertyName] = Generated.NewEntitlementId(),
                [neverMappedBlockName] = new JsonObject
                {
                    [neverMappedNestedName] = neverMappedNestedValue,
                },
                [neverMappedName] = neverMappedValue,
            })));
        var session = await ReadySessionAsync(handler);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        using var raw = JsonDocument.Parse(Assert.Single(entitlements).Raw);
        Assert.Equal(neverMappedValue, raw.RootElement.GetProperty(neverMappedName).GetInt32());
        Assert.Equal(
            neverMappedNestedValue,
            raw.RootElement.GetProperty(neverMappedBlockName).GetProperty(neverMappedNestedName).GetString());
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
        var recoveredAccessToken = Generated.NewAccessToken();
        var recoveredEntitlementId = Generated.NewEntitlementId();
        var exchange = new[]
        {
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            RedirectTo(RedirectCarryingAuthorizationCode(Generated.NewAuthorizationCode())),
            Json(TokenEndpointJson(recoveredAccessToken)),
            Json(SinglePage(new PsnEntitlementPayload { Id = recoveredEntitlementId })),
        };
        var handler = StubHttpMessageHandler.Sequence(exchange);
        var session = await PsnSession.RestoreAsync(
            Generated.NewNpsso(),
            SeededStore(),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
        var client = new PsnLibraryClient();

        // Act
        var entitlements = await client.EntitlementsAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(recoveredEntitlementId, Assert.Single(entitlements).EntitlementId);
        Assert.Equal(exchange.Length, handler.Requests.Count);
        Assert.Equal(
            $"{PsnSession.BearerScheme} {recoveredAccessToken}",
            handler.Requests[^1].Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task DownloadSizesAsync_AsksTheWebStoreForTheDrmDefinitionsAndPagesByStartAndSize()
    {
        // Arrange
        var firstPage = Enumerable.Range(0, PsnLibraryClient.PageSize).Select(_ => SizedGame(Generated.NewPs3EntitlementId())).ToArray();
        var secondPage = new[] { SizedGame(Generated.NewPs3EntitlementId()) };
        var pages = new[] { firstPage, secondPage };
        var total = pages.Sum(page => page.Length);
        var handler = StubHttpMessageHandler.Sequence(pages.Select(page => Json(CommercePage(total, page))).ToArray());
        var session = await ReadySessionAsync(handler);

        // Act
        var sizes = await new PsnLibraryClient().DownloadSizesAsync(session, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(total, sizes.Count);
        Assert.Equal(pages.Length, handler.Requests.Count);
        Assert.StartsWith(PsnLibraryClient.DownloadSizesUrl, handler.Requests[0].RequestUri?.OriginalString, StringComparison.Ordinal);
        Assert.Contains(QueryPair(PsnLibraryClient.StartQueryKey, 0), handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains(QueryPair(PsnLibraryClient.StartQueryKey, PsnLibraryClient.PageSize), handler.Requests[1].RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains(QueryPair(PsnLibraryClient.SizeQueryKey, PsnLibraryClient.PageSize), handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains(QueryPair(PsnLibraryClient.DownloadSizesFieldsQueryKey, PsnLibraryClient.DownloadSizesRequestedFields), handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void MapDownloadSize_SumsEveryGamePackageAndNamesThePlatformFromTheTitleIdInsideTheEntitlementId()
    {
        // Arrange
        var entitlementId = Generated.NewPs3EntitlementId();
        var firstPackage = Generated.NewDownloadSizeBytes();
        var secondPackage = Generated.NewDownloadSizeBytes();
        var entitlement = new PsnCommerceEntitlement
        {
            Id = entitlementId,
            DrmDefinition = new PsnDrmDefinition
            {
                ContentType = PsnLibraryClient.GameContentType,
                Contents = [new PsnDrmContent { ContentSize = firstPackage }, new PsnDrmContent { ContentSize = secondPackage }],
            },
        };

        // Act
        var size = PsnLibraryClient.MapDownloadSize(entitlement);

        // Assert
        Assert.NotNull(size);
        Assert.Equal(entitlementId, size.EntitlementId);
        Assert.Equal(entitlementId.Split('-')[1], size.TitleId);
        Assert.Equal(TitlePlatform.Ps3, size.Platform);
        Assert.Equal(firstPackage + secondPackage, size.Bytes);
    }

    [Fact]
    public void MapDownloadSize_IgnoresVideoContent_ANonTitleEntitlement_AndAnEntitlementWithNoDrmDefinition()
    {
        // Arrange
        var video = SizedGame(Generated.NewPs3EntitlementId()) with
        {
            DrmDefinition = new PsnDrmDefinition { ContentType = Generated.NewToken(), Contents = [new PsnDrmContent { ContentSize = Generated.NewDownloadSizeBytes() }] },
        };
        var nonTitle = SizedGame(Generated.NewNonTitleEntitlementId());
        var withoutDrm = new PsnCommerceEntitlement { Id = Generated.NewPs3EntitlementId() };

        // Act
        var sizes = new[] { video, nonTitle, withoutDrm }.Select(PsnLibraryClient.MapDownloadSize);

        // Assert
        Assert.Equal([null, null, null], sizes);
    }

    [Fact]
    public void MapDownloadSize_IgnoresAGamePackageReportingNoBytes()
    {
        // Arrange
        var empty = SizedGame(Generated.NewPs3EntitlementId()) with
        {
            DrmDefinition = new PsnDrmDefinition { ContentType = PsnLibraryClient.GameContentType, Contents = [new PsnDrmContent { ContentSize = 0 }] },
        };

        // Act
        var size = PsnLibraryClient.MapDownloadSize(empty);

        // Assert
        Assert.Null(size);
    }

    private static PsnCommerceEntitlement SizedGame(string entitlementId) => new()
    {
        Id = entitlementId,
        DrmDefinition = new PsnDrmDefinition
        {
            ContentType = PsnLibraryClient.GameContentType,
            Contents = [new PsnDrmContent { ContentSize = Generated.NewDownloadSizeBytes() }],
        },
    };

    private static string CommercePage(int totalResults, params PsnCommerceEntitlement[] entitlements) =>
        JsonSerializer.Serialize(
            new PsnCommerceEntitlementsResponse { TotalResults = totalResults, Entitlements = entitlements },
            PsnWireFormat);

    private static string Page(int? totalResults, params PsnEntitlementPayload[] entitlements) =>
        JsonSerializer.Serialize(
            new PsnEntitlementsResponse
            {
                TotalResults = totalResults,
                Entitlements = [.. entitlements.Select(entitlement => JsonSerializer.SerializeToElement(entitlement, PsnWireFormat))],
            },
            PsnWireFormat);

    private static string SinglePage(PsnEntitlementPayload entitlement) => Page(1, entitlement);

    private static string PageWithoutTheEntitlementsKey() =>
        new JsonObject { [PsnEntitlementsResponse.TotalResultsPropertyName] = 0 }.ToJsonString();

    private static string PageCarrying(JsonObject entitlement)
    {
        var entitlements = new JsonArray(entitlement);
        return new JsonObject
        {
            [PsnEntitlementsResponse.TotalResultsPropertyName] = entitlements.Count,
            [PsnEntitlementsResponse.EntitlementsPropertyName] = entitlements,
        }.ToJsonString();
    }

    private static string QueryPair(string key, string value) => $"{key}={value}";

    private static string QueryPair(string key, int value) =>
        $"{key}={value.ToString(CultureInfo.InvariantCulture)}";

    private static string RedirectCarryingAuthorizationCode(string authorizationCode) =>
        $"{PsnSession.RedirectUri}?{PsnSession.AuthorizationCodeQueryKey}={authorizationCode}";

    private static PsnEntitlementPayload[] Entries(int count, int firstIndex) =>
        [.. Enumerable
            .Range(firstIndex, count)
            .Select(index => new PsnEntitlementPayload { Id = EntitlementIdAt(index) })];

    private static string EntitlementIdAt(int index) =>
        $"{EntitlementIdPrefix}{index.ToString(CultureInfo.InvariantCulture)}";

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
                AccessToken = Generated.NewAccessToken(),
                ExpiresIn = expiresInSeconds,
                AccessTokenExpiresAt = Generated.NewUnexpiredAccessTokenExpiry(),
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

    private static string TokenEndpointJson(string accessToken)
    {
        var expiresInSeconds = Random.Shared.Next(1, 90_000);
        return JsonSerializer.Serialize(new PsnTokenEndpointResponse
        {
            AccessToken = accessToken,
            RefreshToken = Generated.NewRefreshToken(),
            ExpiresIn = expiresInSeconds,
        });
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json),
        };
}
