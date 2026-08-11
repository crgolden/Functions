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

    [Fact]
    public async Task GeocodeAsync_NoCityAndNoStreet_ReturnsZeroWithoutHttp()
    {
        // Arrange
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)));
        var req = FullRequest with { City = null, Street = null };

        // Act
        var (lat, lng) = await worker.GeocodeAsync(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeAsync_RequestHasCoordinates_ReturnsThemWithoutHttp()
    {
        // Arrange
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Throws(new HttpRequestException("Census must not be called")));
        var req = FullRequest with { Latitude = 39.7392m, Longitude = -104.9903m };

        // Act
        var (lat, lng) = await worker.GeocodeAsync(req, TestContext.Current.CancellationToken);

        // Assert
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

        // Assert
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeCampusesAsync_FillsMissingCoordinatesFromCensus()
    {
        // Arrange
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

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ValidPayload_GeocodesUpsertsThenCompletes()
    {
        // Arrange
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

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal(33.4484m, insert.Parameters["@Lat"].Value);
        Assert.Equal(-112.0740m, insert.Parameters["@Lng"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_FullStateName_NormalizesBeforeWrite()
    {
        // Arrange
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

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal("AZ", insert.Parameters["@State"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableState_CompletesWithoutGeocodingOrWriting()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var payload = FullRequest with { State = null };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableZip_CompletesWithoutGeocodingOrWriting()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var payload = FullRequest with { Zip = null };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_MissingZipButBackfillSucceeds_WritesWithBackfilledZip()
    {
        // Arrange
        const string zipResponseJson = """
            {"places":[{"post code":"85001"}]}
            """;
        const string censusResponseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-112.0740,"y":33.4484}}]}}
            """;
        var handler = StubHttpMessageHandler.Sequence(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(zipResponseJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(censusResponseJson) });
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(handler, connection);
        var payload = FullRequest with { Zip = null };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal("85001", insert.Parameters["@Zip"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableCanonicalName_CompletesWithoutGeocodingOrWriting()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var payload = FullRequest with { CanonicalName = null };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableCity_CompletesWithoutGeocodingOrWriting()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var payload = FullRequest with { City = string.Empty };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_BlankPrimaryLanguage_WritesWithEnglishDefault()
    {
        // Arrange
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-112.0740,"y":33.4484}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse), connection);
        var payload = FullRequest with { PrimaryLanguage = null! };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal("English", insert.Parameters["@Lang"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WorshipStyleOutOfRange_ClampsToZeroAndWrites()
    {
        // Arrange
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-112.0740,"y":33.4484}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse), connection);
        var payload = FullRequest with { WorshipStyle = 99 };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        Assert.Equal(0, insert.Parameters["@Ws"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GeocodeAsync_InvalidCoordinates_FallsBackToCensus()
    {
        // Arrange
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-104.9903,"y":39.7392}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse));
        var req = FullRequest with { Latitude = 200m, Longitude = -104.9903m };

        // Act
        var (lat, lng) = await worker.GeocodeAsync(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(39.7392m, lat);
        Assert.Equal(-104.9903m, lng);
    }

    [Fact]
    public async Task GeocodeCampusesAsync_InvalidCampusCoordinates_FallsBackToCensus()
    {
        // Arrange
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-104.9903,"y":39.7392}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse));
        IReadOnlyList<CampusData> campuses = [new CampusData("North", "1 N St", "Denver", "CO", "80201", 200m, -104.9903m)];

        // Act
        var resolved = await worker.GeocodeCampusesAsync(campuses, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(39.7392m, resolved[0].Latitude);
        Assert.Equal(-104.9903m, resolved[0].Longitude);
    }

    [Fact]
    public async Task Run_ExplicitNullCollectionsInPayload_NormalizesToEmptyAndWrites()
    {
        // Arrange
        const string responseJson = """
            {"result":{"addressMatches":[{"coordinates":{"x":-112.0740,"y":33.4484}}]}}
            """;
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(httpResponse), connection);
        const string json = """
            {
              "CrawlSourceId": "5e0f0a3a-2222-4c1e-8b0a-000000000001",
              "CanonicalName": "Grace Church",
              "Street": "123 Main St",
              "City": "Phoenix",
              "State": "AZ",
              "Zip": "85001",
              "PhoneNumber": null,
              "Website": null,
              "EmailAddress": null,
              "WorshipStyle": 2,
              "PrimaryLanguage": "English",
              "AcceptsLGBTQ": null,
              "WheelchairAccessible": null,
              "HasNursery": null,
              "HasYouthProgram": null,
              "Confidence": 0.9,
              "Latitude": null,
              "Longitude": null,
              "DenominationName": null,
              "Attributes": null,
              "ServiceSchedules": null,
              "Ministries": null,
              "Campuses": null
            }
            """;
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(json));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c => c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
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