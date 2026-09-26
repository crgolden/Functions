namespace Functions.Tests.Unit;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Functions.Churches;
using Functions.Churches.Geocoding;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Moq;
using static Functions.Tests.Unit.GeocoderWorkerFixtureConstants;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class GeocoderWorkerTests
{
    [Fact]
    public void ParseCensusResponse_OneMatch_ReturnsLatLng()
    {
        // Arrange
        var matchedLatitude = Generated.NewGeocodedLatitude();
        var matchedLongitude = Generated.NewGeocodedLongitude();

        // Act
        var (lat, lng) = GeocoderWorker.ParseCensusResponse(CensusResponse(matchedLatitude, matchedLongitude));

        // Assert
        Assert.Equal(matchedLatitude, lat);
        Assert.Equal(matchedLongitude, lng);
    }

    [Fact]
    public void ParseCensusResponse_EmptyMatchArray_ReturnsZeroZero()
    {
        // Act
        var (lat, lng) = GeocoderWorker.ParseCensusResponse(CensusResponseWithoutMatches());

        // Assert
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeAsync_NoCityAndNoStreet_ReturnsZeroWithoutHttp()
    {
        // Arrange
        var worker = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)));
        var req = NewFullRequest() with { City = null, Street = null };

        // Act
        var (lat, lng) = await worker.GeocodeAsync(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeAsync_NoStreet_ReturnsZeroWithoutAskingCensus()
    {
        // Arrange
        var worker = BuildWorker(CensusHandler(Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude()));
        var req = NewFullRequest() with { Street = null };

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
        var suppliedLatitude = Generated.NewGeocodedLatitude();
        var suppliedLongitude = Generated.NewGeocodedLongitude();
        var worker = BuildWorker(StubHttpMessageHandler.Throws(new HttpRequestException(NewErrorMessage())));
        var req = NewFullRequest() with { Latitude = suppliedLatitude, Longitude = suppliedLongitude };

        // Act
        var (lat, lng) = await worker.GeocodeAsync(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(suppliedLatitude, lat);
        Assert.Equal(suppliedLongitude, lng);
    }

    [Fact]
    public async Task GeocodeAsync_HttpReturnsMatch_ReturnsCoordinates()
    {
        // Arrange
        var matchedLatitude = Generated.NewGeocodedLatitude();
        var matchedLongitude = Generated.NewGeocodedLongitude();
        var worker = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude));

        // Act
        var (lat, lng) = await worker.GeocodeAsync(NewFullRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(matchedLatitude, lat);
        Assert.Equal(matchedLongitude, lng);
    }

    [Fact]
    public async Task GeocodeAsync_HttpReturnsNonSuccess_ReturnsZeroZero()
    {
        // Arrange
        var worker = BuildWorker(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        // Act
        var (lat, lng) = await worker.GeocodeAsync(NewFullRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeAsync_HttpThrows_ReturnsZeroZero()
    {
        // Arrange
        var worker = BuildWorker(StubHttpMessageHandler.Throws(new HttpRequestException(NewErrorMessage())));

        // Act
        var (lat, lng) = await worker.GeocodeAsync(NewFullRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0m, lat);
        Assert.Equal(0m, lng);
    }

    [Fact]
    public async Task GeocodeCampusesAsync_FillsMissingCoordinatesFromCensus()
    {
        // Arrange
        var matchedLatitude = Generated.NewGeocodedLatitude();
        var matchedLongitude = Generated.NewGeocodedLongitude();
        var worker = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude));
        IReadOnlyList<CampusData> campuses =
            [new CampusData(Generated.NewCampusName(), Generated.NewStreet(), Generated.NewCity(), Generated.NewStateCodeText(), Generated.NewZip())];

        // Act
        var resolved = await worker.GeocodeCampusesAsync(campuses, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(resolved);
        Assert.Equal(matchedLatitude, resolved[0].Latitude);
        Assert.Equal(matchedLongitude, resolved[0].Longitude);
    }

    [Fact]
    public async Task GeocodeAsync_InvalidCoordinates_FallsBackToCensus()
    {
        // Arrange
        var matchedLatitude = Generated.NewGeocodedLatitude();
        var matchedLongitude = Generated.NewGeocodedLongitude();
        var worker = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude));
        var req = NewFullRequest() with { Latitude = NewOutOfRangeLatitude(), Longitude = matchedLongitude };

        // Act
        var (lat, lng) = await worker.GeocodeAsync(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(matchedLatitude, lat);
        Assert.Equal(matchedLongitude, lng);
    }

    [Fact]
    public async Task GeocodeCampusesAsync_InvalidCampusCoordinates_FallsBackToCensus()
    {
        // Arrange
        var matchedLatitude = Generated.NewGeocodedLatitude();
        var matchedLongitude = Generated.NewGeocodedLongitude();
        var worker = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude));
        IReadOnlyList<CampusData> campuses =
        [
            new CampusData(
                Generated.NewCampusName(), Generated.NewStreet(), Generated.NewCity(), Generated.NewStateCodeText(), Generated.NewZip(), NewOutOfRangeLatitude(), matchedLongitude),
        ];

        // Act
        var resolved = await worker.GeocodeCampusesAsync(campuses, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(matchedLatitude, resolved[0].Latitude);
        Assert.Equal(matchedLongitude, resolved[0].Longitude);
    }

    [Fact]
    public async Task Run_NullPayload_DeadLettersMessageWithoutDb()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(new FakeHttpClientFactory(), connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson<GeocodingRequest?>(null));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(
            a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_ValidPayload_GeocodesUpsertsThenCompletes()
    {
        // Arrange
        var matchedLatitude = Generated.NewGeocodedLatitude();
        var matchedLongitude = Generated.NewGeocodedLongitude();
        var connection = new FakeDbConnection();
        var worker = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude), connection);
        var message = MessageFor(NewFullRequest());
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(matchedLatitude, insert.Parameters[ChurchSqlParameters.Lat].Value);
        Assert.Equal(matchedLongitude, insert.Parameters[ChurchSqlParameters.Lng].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_FullStateName_NormalizesBeforeWrite()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(CensusHandler(Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude()), connection);
        var message = MessageFor(NewFullRequest() with { State = FullStateName });
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(FullStateCode, insert.Parameters[ChurchSqlParameters.State].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableState_CompletesWithoutGeocodingOrWriting()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var message = MessageFor(NewFullRequest() with { State = null });
        var actions = CompletingActions(message);

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
        var worker = BuildWorker(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var message = MessageFor(NewFullRequest() with { Zip = null });
        var actions = CompletingActions(message);

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
        var backfilledZip = Generated.NewZip();
        var handler = StubHttpMessageHandler.Sequence(
            JsonResponse(ZipLookupResponse(backfilledZip)),
            JsonResponse(CensusResponse(Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude())));
        var connection = new FakeDbConnection();
        var worker = BuildWorker(handler, connection);
        var message = MessageFor(NewFullRequest() with { Zip = null });
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(backfilledZip, insert.Parameters[ChurchSqlParameters.Zip].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableCanonicalName_CompletesWithoutGeocodingOrWriting()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var message = MessageFor(NewFullRequest() with { CanonicalName = null });
        var actions = CompletingActions(message);

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
        var worker = BuildWorker(
            StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)), connection);
        var message = MessageFor(NewFullRequest() with { City = string.Empty });
        var actions = CompletingActions(message);

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
        var connection = new FakeDbConnection();
        var worker = BuildWorker(CensusHandler(Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude()), connection);
        var payloadNode = NodeFor(NewFullRequest());
        payloadNode[nameof(GeocodingRequest.PrimaryLanguage)] = null;
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(payloadNode.ToJsonString()));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(ChurchDefaults.PrimaryLanguage, insert.Parameters[ChurchSqlParameters.Lang].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WorshipStyleOutOfRange_ClampsToZeroAndWrites()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(CensusHandler(Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude()), connection);
        var message = MessageFor(NewFullRequest() with { WorshipStyle = NewOutOfRangeWorshipStyle() });
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(ChurchWorshipStyles.Unknown, insert.Parameters[ChurchSqlParameters.Ws].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ExplicitNullCollectionsInPayload_NormalizesToEmptyAndWrites()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(CensusHandler(Generated.NewGeocodedLatitude(), Generated.NewGeocodedLongitude()), connection);
        var payloadNode = NodeFor(NewFullRequest());
        payloadNode[nameof(GeocodingRequest.Attributes)] = null;
        payloadNode[nameof(GeocodingRequest.ServiceSchedules)] = null;
        payloadNode[nameof(GeocodingRequest.Ministries)] = null;
        payloadNode[nameof(GeocodingRequest.Campuses)] = null;
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(payloadNode.ToJsonString()));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(connection.ExecutedCommands, c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static FakeDbCommand SingleChurchInsert(FakeDbConnection connection) =>
        connection.ExecutedCommands.Single(c =>
            c.CommandText.Contains("INSERT INTO [dbo].[Churches]", StringComparison.Ordinal));

    private static Mock<ServiceBusMessageActions> CompletingActions(ServiceBusReceivedMessage message)
    {
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return actions;
    }

    private static ServiceBusReceivedMessage MessageFor(GeocodingRequest request) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(request));

    private static JsonObject NodeFor(GeocodingRequest request) =>
        Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(request));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static StubHttpMessageHandler CensusHandler(decimal latitude, decimal longitude) =>
        StubHttpMessageHandler.Returns(JsonResponse(CensusResponse(latitude, longitude)));

    private static string CensusResponse(decimal latitude, decimal longitude) =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [CensusGeocoderFields.Result] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [CensusGeocoderFields.AddressMatches] = new[]
                {
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [CensusGeocoderFields.Coordinates] = new Dictionary<string, decimal>(StringComparer.Ordinal)
                        {
                            [CensusGeocoderFields.Longitude] = longitude,
                            [CensusGeocoderFields.Latitude] = latitude,
                        },
                    },
                },
            },
        });

    private static string CensusResponseWithoutMatches() =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [CensusGeocoderFields.Result] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [CensusGeocoderFields.AddressMatches] = Array.Empty<object>(),
            },
        });

    private static string ZipLookupResponse(string postCode) =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ZipLookupFields.Places] = new[]
            {
                new Dictionary<string, string>(StringComparer.Ordinal) { [ZipLookupFields.PostCode] = postCode },
            },
        });

    private static GeocoderWorker BuildWorker(
        HttpMessageHandler handler,
        FakeDbConnection? connection = null)
    {
        connection ??= new FakeDbConnection();
        var factory = new FakeHttpClientFactory(handler);
        return BuildWorker(factory, connection);
    }

    private static GeocoderWorker BuildWorker(
        IHttpClientFactory factory,
        FakeDbConnection connection)
    {
        var censusGeocoderUrl = $"https://{Guid.NewGuid():N}.example/geocoder/locations/address";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new(ChurchSettingKeys.CensusGeocoderUrl, censusGeocoderUrl)])
            .Build();
        return new GeocoderWorker(factory, new ChurchWriter(connection, FakeServiceBus.CreateSenders().Senders), config, TelemetryHarness.Shared.Telemetry);
    }

    private static GeocodingRequest NewFullRequest() => new(
        CrawlSourceId: Generated.NewCrawlSourceId(),
        CanonicalName: Generated.NewChurchName(),
        Street: Generated.NewStreet(),
        City: Generated.NewCity(),
        State: Generated.NewStateCodeText(),
        Zip: Generated.NewZip(),
        PhoneNumber: Generated.NewPhoneNumber(),
        Website: Generated.NewWebsite(),
        EmailAddress: Generated.NewEmailAddress(),
        WorshipStyle: NewWorshipStyleCodeOtherThanUnknown(),
        PrimaryLanguage: Generated.NewLanguageName(),
        AcceptsLGBTQ: true,
        WheelchairAccessible: false,
        HasNursery: true,
        HasYouthProgram: false,
        Confidence: Generated.NewConfidence());

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
