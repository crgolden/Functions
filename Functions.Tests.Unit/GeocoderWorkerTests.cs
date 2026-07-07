namespace Functions.Tests.Unit;

using System.Net;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class GeocoderWorkerTests
{
    private static readonly GeocodingRequest FullRequest = new(
        CrawlSourceId: Guid.NewGuid(),
        CanonicalName: "Grace Church",
        Street: "123 Main St",
        City: "Phoenix",
        State: "AZ",
        Zip: "85001",
        PhoneNumber: "602-555-1212",
        Website: "https://grace.example",
        EmailAddress: "info@grace.example",
        WorshipStyle: 2,
        PrimaryLanguage: "English",
        AcceptsLGBTQ: true,
        WheelchairAccessible: false,
        HasNursery: true,
        HasYouthProgram: false,
        Confidence: 0.9m);

    // --- ParseCensusResponse (pure, internal static) ---
    [Fact]
    public void ParseCensusResponse_OneMatch_ReturnsLatLng()
    {
        // Arrange
        const string json = """
            {"result":{"addressMatches":[{"coordinates":{"x":-112.0740,"y":33.4484}}]}}
            """;

        // Act
        var (lat, lng) = GeocoderWorker.ParseCensusResponse(json);

        // Assert
        Assert.Equal(33.4484m, lat);
        Assert.Equal(-112.0740m, lng);
    }

    [Fact]
    public void ParseCensusResponse_EmptyMatchArray_ReturnsZeroZero()
    {
        // Arrange
        const string json = """{"result":{"addressMatches":[]}}""";

        // Act
        var (lat, lng) = GeocoderWorker.ParseCensusResponse(json);

        // Assert
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    // --- GeocodeAsync (internal instance) ---
    [Fact]
    public async Task GeocodeAsync_NoCityAndNoStreet_ReturnsZeroWithoutHttp()
    {
        // Arrange
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)));
        var req = FullRequest with { City = null, Street = null };

        // Act
        var (lat, lng) = await worker.GeocodeAsync(req, TestContext.Current.CancellationToken);

        // Assert — no HTTP call; returns 0,0
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeAsync_RequestHasCoordinates_ReturnsThemWithoutHttp()
    {
        // Arrange — a request that already carries coordinates (e.g. from OSM) must bypass Census
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Throws(new HttpRequestException("Census must not be called")));
        var req = FullRequest with { Latitude = 39.7392m, Longitude = -104.9903m };

        // Act
        var (lat, lng) = await worker.GeocodeAsync(req, TestContext.Current.CancellationToken);

        // Assert — provided coordinates are returned; no HTTP call made
        Assert.Equal(39.7392m, lat);
        Assert.Equal(-104.9903m, lng);
    }

    [Fact]
    public async Task GeocodeAsync_HttpReturnsMatch_ReturnsCoordinates()
    {
        // Arrange
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-112.0740,"y":33.4484}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse));

        // Act
        var (lat, lng) = await worker.GeocodeAsync(FullRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(33.4484m, lat);
        Assert.Equal(-112.0740m, lng);
    }

    [Fact]
    public async Task GeocodeAsync_HttpReturnsNonSuccess_ReturnsZeroZero()
    {
        // Arrange
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        // Act
        var (lat, lng) = await worker.GeocodeAsync(FullRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeAsync_HttpThrows_ReturnsZeroZero()
    {
        // Arrange
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Throws(new HttpRequestException("boom")));

        // Act
        var (lat, lng) = await worker.GeocodeAsync(FullRequest, TestContext.Current.CancellationToken);

        // Assert — exception swallowed; falls back to 0,0
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeCampusesAsync_FillsMissingCoordinatesFromCensus()
    {
        // Arrange — Census returns a match; the campus has no coordinates yet
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-104.9903,"y":39.7392}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse));
        IReadOnlyList<CampusData> campuses = [new CampusData("North", "1 N St", "Denver", "CO", "80201")];

        // Act
        var resolved = await worker.GeocodeCampusesAsync(campuses, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(resolved);
        Assert.Equal(39.7392m, resolved[0].Latitude);
        Assert.Equal(-104.9903m, resolved[0].Longitude);
    }

    // --- Run orchestration (geocode + delegate to ChurchWriter) ---
    [Fact]
    public async Task Run_NullPayload_DeadLettersMessageWithoutDb()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(new FakeHttpClientFactory(), connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — no DB interaction; message dead-lettered
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ValidPayload_GeocodesUpsertsThenCompletes()
    {
        // Arrange — Census returns a match; connection starts Closed
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-112.0740,"y":33.4484}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse), connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(FullRequest));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — geocoded lat/lng reach the INSERT via ChurchWriter; message completed
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal(33.4484m, insert.Parameters["@Lat"].Value);
        Assert.Equal(-112.0740m, insert.Parameters["@Lng"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_FullStateName_NormalizesBeforeWrite()
    {
        // Arrange — OpenAI enrichment sometimes returns a full state name instead of a 2-letter
        // code; this must be normalized before ChurchBuilder's exact-2-letter validation runs.
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-112.0740,"y":33.4484}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        };
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse), connection);
        var payload = FullRequest with { State = "Arizona" };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — the write carries the normalized 2-letter code, not the raw full name
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal("AZ", insert.Parameters["@State"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableState_DeadLettersWithoutGeocodingOrWriting()
    {
        // Arrange — a record with no resolvable state can never satisfy ChurchBuilder's exact
        // 2-letter invariant; this must dead-letter immediately rather than retry 10 times only to
        // have ChurchWriter throw the same ArgumentException every attempt.
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var payload = FullRequest with { State = null };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, "unresolvable-state", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — no DB write and no Census call ran before the dead-letter
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, "unresolvable-state", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (GeocoderWorker Worker, FakeDbConnection Connection) BuildWorker(
        HttpMessageHandler handler,
        FakeDbConnection? connection = null)
    {
        connection ??= new FakeDbConnection();
        var factory = new FakeHttpClientFactory(handler);
        return BuildWorker(factory, connection);
    }

    private static (GeocoderWorker Worker, FakeDbConnection Connection) BuildWorker(
        IHttpClientFactory factory,
        FakeDbConnection connection)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new("CensusGeocoderUrl", "https://geocoding.geo.census.gov/geocoder/locations/address")])
            .Build();
        return (new GeocoderWorker(factory, new ChurchWriter(connection, FakeServiceBus.Create().Factory), config), connection);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory()
            : this(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)))
        {
        }

        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler);
    }
}
