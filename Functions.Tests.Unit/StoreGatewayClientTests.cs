namespace Functions.Tests.Unit;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Functions.Curator.Psn;
using Functions.Curator.Store;
using Functions.Tests.Unit.TestSupport;

[Trait("Category", "Unit")]
public sealed class StoreGatewayClientTests
{
    private static readonly JsonSerializerOptions StoreWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task ProductAsync_SendsThePersistedQueryAsAGetWithTheStorefrontsHeaders()
    {
        // Arrange
        var productId = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix);
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Body(new StoreProductNode { Id = productId })));

        // Act
        await new StoreGatewayClient(new HttpClient(handler)).ProductAsync(productId, TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        var requestUri = Assert.IsType<Uri>(request.RequestUri);
        var unescapedQuery = Uri.UnescapeDataString(requestUri.Query);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith(StoreGatewayClient.OperationUrl, requestUri.OriginalString, StringComparison.Ordinal);
        Assert.Contains($"{StoreGatewayClient.OperationNameQueryKey}={StoreGatewayClient.ProductOperation}", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains(StoreGatewayClient.ProductHash, unescapedQuery, StringComparison.Ordinal);
        Assert.Contains(productId, unescapedQuery, StringComparison.Ordinal);
        Assert.Equal([StoreGatewayClient.LocaleHeaderValue], request.Headers.GetValues(StoreGatewayClient.LocaleHeaderName));
        Assert.Equal([StoreGatewayClient.PreflightHeaderValue], request.Headers.GetValues(StoreGatewayClient.PreflightHeaderName));
    }

    [Fact]
    public async Task ProductAsync_MapsTheFieldsTheWorkerReads_FromTheStorefrontsProductNode()
    {
        // Arrange
        var node = new StoreProductNode
        {
            Id = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix),
            Name = Generated.NewGameTitle(),
            NpTitleId = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix),
            PublisherName = Generated.NewPublisher(),
            ReleaseDate = Generated.NewReleaseTimestamp(),
            Type = Generated.NewConceptType(),
            ContentRating = new StoreContentRating { Authority = Generated.NewRatingAuthority(), Name = Generated.NewContentRating() },
            Genres = [new StoreLocalizedGenre { Value = Generated.NewGenreDisplayName() }],
            Concept = new StoreConcept { Id = Generated.NewConceptId() },
        };
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Body(node)));

        // Act
        var product = await new StoreGatewayClient(new HttpClient(handler)).ProductAsync(node.Id, TestContext.Current.CancellationToken);

        // Assert
        var mapped = Assert.IsType<StoreProductNode>(product);
        Assert.Equal(node.Id, mapped.Id);
        Assert.Equal(node.Name, mapped.Name);
        Assert.Equal(node.NpTitleId, mapped.NpTitleId);
        Assert.Equal(node.PublisherName, mapped.PublisherName);
        Assert.Equal(node.ReleaseDate, mapped.ReleaseDate);
        Assert.Equal(node.Type, mapped.Type);
        Assert.Equal(node.ContentRating, mapped.ContentRating);
        Assert.Equal(node.Concept, mapped.Concept);
        Assert.Equal(node.Genres.Select(genre => genre.Value), mapped.Genres.Select(genre => genre.Value));
    }

    [Fact]
    public async Task StarRatingAsync_UsesItsOwnPersistedQuery_AndReadsTheAverageAndCount()
    {
        // Arrange
        var productId = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix);
        var rating = new StoreStarRating { AverageRating = Generated.NewStarRating(), TotalRatingsCount = Generated.NewPsnRatingCount() };
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Body(new StoreProductNode { Id = productId, StarRating = rating })));

        // Act
        var starRating = await new StoreGatewayClient(new HttpClient(handler)).StarRatingAsync(productId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rating, starRating);
        var request = Assert.Single(handler.Requests);
        var requestUri = Assert.IsType<Uri>(request.RequestUri);
        Assert.Contains($"{StoreGatewayClient.OperationNameQueryKey}={StoreGatewayClient.StarRatingOperation}", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains(StoreGatewayClient.StarRatingHash, Uri.UnescapeDataString(requestUri.Query), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductAsync_ReturnsNothing_WhenTheStorefrontAnswersWithAnErrorInsteadOfAProduct()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Errors(Generated.NewToken(), Generated.NewErrorMessage())));

        // Act
        var product = await new StoreGatewayClient(new HttpClient(handler)).ProductAsync(Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(product);
    }

    [Theory]
    [InlineData(StoreGatewayClient.PersistedQueryNotFoundCode, null)]
    [InlineData(null, StoreGatewayClient.PersistedQueryNotFoundMessage)]
    public async Task ProductAsync_ThrowsARotatedQuery_WhenTheStorefrontNoLongerKnowsTheHash(string? code, string? message)
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Errors(code, message)));

        // Act
        var exception = await Record.ExceptionAsync(
            () => new StoreGatewayClient(new HttpClient(handler)).ProductAsync(Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<StoreQueryRotatedException>(exception);
    }

    [Fact]
    public async Task ProductAsync_SurfacesAnHttpFailure_RatherThanReadingItAsAMissingProduct()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.BadGateway));

        // Act
        var exception = await Record.ExceptionAsync(
            () => new StoreGatewayClient(new HttpClient(handler)).ProductAsync(Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<HttpRequestException>(exception);
    }

    [Fact]
    public async Task ProductAsync_ReportsTheStoreUnusable_WhenTheBodyIsLiteralNull()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(JsonResponse.NullLiteral));
        var client = new StoreGatewayClient(new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.ProductAsync(Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix), TestContext.Current.CancellationToken));

        // Assert
        const string reason =
            "A body of literal null deserialises to null, and repairing it into an empty response makes it "
            + "indistinguishable from a product node the storefront genuinely does not have. The worker "
            + "would then write psn_attempted with psn_enriched false and never ask again, so a storefront "
            + "fault would be recorded as a settled answer.";

        Assert.True(exception is HttpRequestException, reason);
        var storeFault = Assert.IsType<HttpRequestException>(exception);
        Assert.Equal(StoreGatewayClient.UnusableAnswer(StoreGatewayClient.ProductOperation), storeFault.Message);
    }

    [Fact]
    public async Task ProductAsync_ReportsTheStoreUnusable_RatherThanEndingTheWholeRun_WhenTheBodyIsNotJson()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Generated.NewGameTitle()));
        var client = new StoreGatewayClient(new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.ProductAsync(Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix), TestContext.Current.CancellationToken));

        // Assert
        const string reason =
            "An unparseable body raised JsonException, which StoreProductEnrichmentWorker catches nowhere: "
            + "it escaped ProcessAsync and ended the run. HttpRequestException is the shape the worker "
            + "already maps to store_unreachable, so the pass stops and resumes instead.";

        Assert.True(exception is HttpRequestException, reason);
        var storeFault = Assert.IsType<HttpRequestException>(exception);
        Assert.IsType<JsonException>(storeFault.InnerException);
    }

    [Fact]
    public async Task CategoryPageAsync_AsksForOnePageOfACategory_WithTheFullGameFilterAndTheFirstHash()
    {
        // Arrange
        var categoryId = Guid.NewGuid().ToString();
        var offset = Generated.NewPsnRatingCount();
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Body(new StoreCategoryGrid())));

        // Act
        await new StoreGatewayClient(new HttpClient(handler)).CategoryPageAsync(
            categoryId, offset, StoreCatalogCrawlWorker.PageSize, TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        var requestUri = Assert.IsType<Uri>(request.RequestUri);
        var unescapedQuery = Uri.UnescapeDataString(requestUri.Query);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains($"{StoreGatewayClient.OperationNameQueryKey}={StoreGatewayClient.CategoryGridOperation}", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains(StoreGatewayClient.CategoryGridHashes[0], unescapedQuery, StringComparison.Ordinal);
        Assert.Contains(categoryId, unescapedQuery, StringComparison.Ordinal);
        Assert.Contains(StoreGatewayClient.FullGameFilter, unescapedQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CategoryPageAsync_MapsTheGridTheCrawlWalks_FromTheStorefrontsAnswer()
    {
        // Arrange
        var grid = new StoreCategoryGrid
        {
            ReportingName = Generated.NewGameTitle(),
            PageInfo = new StoreCategoryPageInfo
            {
                Offset = Generated.NewPsnRatingCount(),
                TotalCount = Generated.NewPsnRatingCount(),
                IsLast = true,
            },
            Products = [new StoreCategoryProduct { Id = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix), Name = Generated.NewGameTitle() }],
        };
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Body(grid)));

        var categoryId = Guid.NewGuid().ToString();

        // Act
        var page = await new StoreGatewayClient(new HttpClient(handler)).CategoryPageAsync(
            categoryId, 0, StoreCatalogCrawlWorker.PageSize, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(grid.ReportingName, page.ReportingName);
        Assert.Equal(grid.PageInfo, page.PageInfo);
        Assert.Equal(grid.Products.Select(product => product.Id), page.Products.Select(product => product.Id));
    }

    [Fact]
    public async Task CategoryPageAsync_FallsBackToTheNextHash_WhenTheStorefrontNoLongerKnowsTheFirst()
    {
        // Arrange
        var reportingName = Generated.NewGameTitle();
        var handler = StubHttpMessageHandler.Sequence(
            JsonResponse.Ok(Errors(StoreGatewayClient.PersistedQueryNotFoundCode, null)),
            JsonResponse.Ok(Body(new StoreCategoryGrid { ReportingName = reportingName })));

        var categoryId = Guid.NewGuid().ToString();

        // Act
        var page = await new StoreGatewayClient(new HttpClient(handler)).CategoryPageAsync(
            categoryId, 0, StoreCatalogCrawlWorker.PageSize, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(reportingName, page.ReportingName);
        Assert.Collection(
            handler.Requests,
            first => Assert.Contains(
                StoreGatewayClient.CategoryGridHashes[0],
                Uri.UnescapeDataString(Assert.IsType<Uri>(first.RequestUri).Query),
                StringComparison.Ordinal),
            second => Assert.Contains(
                StoreGatewayClient.CategoryGridHashes[1],
                Uri.UnescapeDataString(Assert.IsType<Uri>(second.RequestUri).Query),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task CategoryPageAsync_ThrowsARotatedQuery_WhenEveryHashIsRejected()
    {
        // Arrange
        var rejections = StoreGatewayClient.CategoryGridHashes
            .Select(_ => JsonResponse.Ok(Errors(StoreGatewayClient.PersistedQueryNotFoundCode, null)))
            .ToArray();
        var handler = StubHttpMessageHandler.Sequence(rejections);

        var categoryId = Guid.NewGuid().ToString();

        // Act
        var exception = await Record.ExceptionAsync(
            () => new StoreGatewayClient(new HttpClient(handler)).CategoryPageAsync(
                categoryId, 0, StoreCatalogCrawlWorker.PageSize, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<StoreQueryRotatedException>(exception);
        Assert.Equal(StoreGatewayClient.CategoryGridHashes.Length, handler.Requests.Count);
    }

    [Fact]
    public async Task CategoryPageAsync_TreatsAnAnswerCarryingNoGrid_AsAnUnusableAnswerRatherThanAnEmptyPage()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(
            JsonResponse.Ok(JsonSerializer.Serialize(new StoreGraphResponse { Data = new StoreGraphData() }, StoreWireFormat)));

        var categoryId = Guid.NewGuid().ToString();

        // Act
        var exception = await Record.ExceptionAsync(
            () => new StoreGatewayClient(new HttpClient(handler)).CategoryPageAsync(
                categoryId, 0, StoreCatalogCrawlWorker.PageSize, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<HttpRequestException>(exception);
    }

    private static string Body(StoreProductNode node) =>
        JsonSerializer.Serialize(new StoreGraphResponse { Data = new StoreGraphData { ProductRetrieve = node } }, StoreWireFormat);

    private static string Body(StoreCategoryGrid grid) =>
        JsonSerializer.Serialize(new StoreGraphResponse { Data = new StoreGraphData { CategoryGridRetrieve = grid } }, StoreWireFormat);

    private static string Errors(string? code, string? message) =>
        JsonSerializer.Serialize(
            new StoreGraphResponse
            {
                Errors = [new StoreGraphError { Message = message, Extensions = code is null ? null : new StoreGraphErrorExtensions { Code = code } }],
            },
            StoreWireFormat);
}
