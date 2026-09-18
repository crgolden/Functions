namespace Functions.Tests.Unit;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.Store;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class StoreGatewayClientTests
{
    private static readonly JsonSerializerOptions StoreWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task ProductAsync_SendsThePersistedQueryAsAGetWithTheStorefrontsHeaders()
    {
        // Arrange
        var productId = TestValues.NewStoreProductId();
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
            Id = TestValues.NewStoreProductId(),
            Name = TestValues.NewGameTitle(),
            NpTitleId = TestValues.NewTitleId(),
            PublisherName = TestValues.NewPublisher(),
            ReleaseDate = TestValues.NewReleaseTimestamp(),
            Type = TestValues.NewConceptType(),
            ContentRating = new StoreContentRating { Authority = TestValues.NewRatingAuthority(), Name = TestValues.NewContentRating() },
            Genres = [new StoreLocalizedGenre { Value = TestValues.NewGenreDisplayName() }],
            Concept = new StoreConcept { Id = TestValues.NewConceptId() },
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
        var productId = TestValues.NewStoreProductId();
        var rating = new StoreStarRating { AverageRating = TestValues.NewStarRating(), TotalRatingsCount = TestValues.NewPsnRatingCount() };
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
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(Errors(TestValues.NewToken(), TestValues.NewErrorMessage())));

        // Act
        var product = await new StoreGatewayClient(new HttpClient(handler)).ProductAsync(TestValues.NewStoreProductId(), TestContext.Current.CancellationToken);

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
            () => new StoreGatewayClient(new HttpClient(handler)).ProductAsync(TestValues.NewStoreProductId(), TestContext.Current.CancellationToken));

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
            () => new StoreGatewayClient(new HttpClient(handler)).ProductAsync(TestValues.NewStoreProductId(), TestContext.Current.CancellationToken));

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
            () => client.ProductAsync(TestValues.NewStoreProductId(), TestContext.Current.CancellationToken));

        // Assert
        const string reason =
            "A body of literal null deserialises to null, and repairing it into an empty response makes it "
            + "indistinguishable from a product node the storefront genuinely does not have. The worker "
            + "would then write psn_attempted with psn_enriched false and never ask again, so a storefront "
            + "fault would be recorded as a settled answer.";

        Assert.True(exception is HttpRequestException, reason);
        Assert.Equal(StoreGatewayClient.UnusableAnswer(StoreGatewayClient.ProductOperation), exception?.Message);
    }

    [Fact]
    public async Task ProductAsync_ReportsTheStoreUnusable_RatherThanEndingTheWholeRun_WhenTheBodyIsNotJson()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(JsonResponse.Ok(TestValues.NewGameTitle()));
        var client = new StoreGatewayClient(new HttpClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(
            () => client.ProductAsync(TestValues.NewStoreProductId(), TestContext.Current.CancellationToken));

        // Assert
        const string reason =
            "An unparseable body raised JsonException, which StoreProductEnrichmentWorker catches nowhere: "
            + "it escaped ProcessAsync and ended the run. HttpRequestException is the shape the worker "
            + "already maps to store_unreachable, so the pass stops and resumes instead.";

        Assert.True(exception is HttpRequestException, reason);
        Assert.IsType<JsonException>(exception?.InnerException);
    }

    private static string Body(StoreProductNode node) =>
        JsonSerializer.Serialize(new StoreGraphResponse { Data = new StoreGraphData { ProductRetrieve = node } }, StoreWireFormat);

    private static string Errors(string? code, string? message) =>
        JsonSerializer.Serialize(
            new StoreGraphResponse
            {
                Errors = [new StoreGraphError { Message = message, Extensions = code is null ? null : new StoreGraphErrorExtensions { Code = code } }],
            },
            StoreWireFormat);
}
