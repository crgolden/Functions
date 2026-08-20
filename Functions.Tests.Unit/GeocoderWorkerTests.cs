namespace Functions.Tests.Unit;

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Churches;
using Churches.Geocoding;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class GeocoderWorkerTests
{
    private const string FullStateName = "Arizona";

    private const string FullStateCode = "AZ";

    [Fact]
    public void ParseCensusResponse_OneMatch_ReturnsLatLng()
    {
        // Arrange
        var matchedLatitude = NewLatitude();
        var matchedLongitude = NewLongitude();

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
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)));
        var req = NewFullRequest() with { City = null, Street = null };

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
        var suppliedLatitude = NewLatitude();
        var suppliedLongitude = NewLongitude();
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Throws(new HttpRequestException(NewFailureMessage())));
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
        var matchedLatitude = NewLatitude();
        var matchedLongitude = NewLongitude();
        var (worker, _) = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude));

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
        var (worker, _) = BuildWorker(
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
        var (worker, _) = BuildWorker(StubHttpMessageHandler.Throws(new HttpRequestException(NewFailureMessage())));

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
        var matchedLatitude = NewLatitude();
        var matchedLongitude = NewLongitude();
        var (worker, _) = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude));
        IReadOnlyList<CampusData> campuses =
            [new CampusData(NewCampusName(), NewStreet(), NewCity(), NewStateCode(), NewZip())];

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
        var matchedLatitude = NewLatitude();
        var matchedLongitude = NewLongitude();
        var (worker, _) = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude));
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
        var matchedLatitude = NewLatitude();
        var matchedLongitude = NewLongitude();
        var (worker, _) = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude));
        IReadOnlyList<CampusData> campuses =
        [
            new CampusData(
                NewCampusName(), NewStreet(), NewCity(), NewStateCode(), NewZip(), NewOutOfRangeLatitude(), matchedLongitude),
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
        var (worker, _) = BuildWorker(new FakeHttpClientFactory(), connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
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
        var matchedLatitude = NewLatitude();
        var matchedLongitude = NewLongitude();
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(CensusHandler(matchedLatitude, matchedLongitude), connection);
        var message = MessageFor(NewFullRequest());
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(matchedLatitude, insert.Parameters["@Lat"].Value);
        Assert.Equal(matchedLongitude, insert.Parameters["@Lng"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_FullStateName_NormalizesBeforeWrite()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(CensusHandler(NewLatitude(), NewLongitude()), connection);
        var message = MessageFor(NewFullRequest() with { State = FullStateName });
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(FullStateCode, insert.Parameters["@State"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableState_CompletesWithoutGeocodingOrWriting()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(
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
        var (worker, _) = BuildWorker(
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
        var backfilledZip = NewZip();
        var handler = StubHttpMessageHandler.Sequence(
            JsonResponse(ZipLookupResponse(backfilledZip)),
            JsonResponse(CensusResponse(NewLatitude(), NewLongitude())));
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(handler, connection);
        var message = MessageFor(NewFullRequest() with { Zip = null });
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(backfilledZip, insert.Parameters["@Zip"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_UnresolvableCanonicalName_CompletesWithoutGeocodingOrWriting()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(
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
        var (worker, _) = BuildWorker(
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
        var (worker, _) = BuildWorker(CensusHandler(NewLatitude(), NewLongitude()), connection);
        var payloadNode = NodeFor(NewFullRequest());
        payloadNode[nameof(GeocodingRequest.PrimaryLanguage)] = null;
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(payloadNode.ToJsonString()));
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(ChurchDefaults.PrimaryLanguage, insert.Parameters["@Lang"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WorshipStyleOutOfRange_ClampsToZeroAndWrites()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(CensusHandler(NewLatitude(), NewLongitude()), connection);
        var message = MessageFor(NewFullRequest() with { WorshipStyle = NewOutOfRangeWorshipStyle() });
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = SingleChurchInsert(connection);
        Assert.Equal(ChurchWorshipStyles.Unknown, insert.Parameters["@Ws"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ExplicitNullCollectionsInPayload_NormalizesToEmptyAndWrites()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(CensusHandler(NewLatitude(), NewLongitude()), connection);
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

    private static HttpMessageHandler CensusHandler(decimal latitude, decimal longitude) =>
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
        var censusGeocoderUrl = $"https://{Guid.NewGuid():N}.example/geocoder/locations/address";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new(ChurchSettingKeys.CensusGeocoderUrl, censusGeocoderUrl)])
            .Build();
        return (new GeocoderWorker(factory, new ChurchWriter(connection, FakeServiceBus.Create().Factory), config), connection);
    }

    private static GeocodingRequest NewFullRequest() => new(
        CrawlSourceId: Guid.NewGuid(),
        CanonicalName: NewChurchName(),
        Street: NewStreet(),
        City: NewCity(),
        State: NewStateCode(),
        Zip: NewZip(),
        PhoneNumber: NewPhoneNumber(),
        Website: $"https://{LowercaseToken(12)}.example",
        EmailAddress: NewEmailAddress(),
        WorshipStyle: Random.Shared.Next(1, 6),
        PrimaryLanguage: $"language{LowercaseToken(8)}",
        AcceptsLGBTQ: true,
        WheelchairAccessible: false,
        HasNursery: true,
        HasYouthProgram: false,
        Confidence: Math.Round((decimal)Random.Shared.NextDouble(), 2));

    private static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    private static string NewChurchName() => $"church{LowercaseToken(12)}";

    private static string NewCity() => $"city{LowercaseToken(12)}";

    private static string NewCampusName() => $"campus{LowercaseToken(12)}";

    private static string NewStreet() =>
        $"{Random.Shared.Next(100, 10000).ToString(CultureInfo.InvariantCulture)} {LowercaseToken(10)} street";

    private static string NewStateCode() =>
        $"{(char)Random.Shared.Next('A', 'Z' + 1)}{(char)Random.Shared.Next('A', 'Z' + 1)}";

    private static string NewZip() => Random.Shared.Next(10000, 100000).ToString(CultureInfo.InvariantCulture);

    private static string NewPhoneNumber() =>
        $"{Random.Shared.Next(200, 1000)}-{Random.Shared.Next(200, 1000)}-{Random.Shared.Next(1000, 10000)}";

    private static string NewEmailAddress() => $"{LowercaseToken(10)}@{LowercaseToken(10)}.example";

    private static string NewFailureMessage() => $"failure{Guid.NewGuid():N}";

    private static decimal NewLatitude() => Math.Round(((decimal)Random.Shared.NextDouble() * 40m) + 1m, 4);

    private static decimal NewLongitude() => -Math.Round(((decimal)Random.Shared.NextDouble() * 100m) + 1m, 4);

    private static decimal NewOutOfRangeLatitude() => Random.Shared.Next(91, 1000);

    private static int NewOutOfRangeWorshipStyle() => Random.Shared.Next(6, 1000);

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
