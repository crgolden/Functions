namespace Functions.Tests;

using System.Data;
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

    // --- UpsertChurchAsync (internal instance; FakeDbConnection) ---
    [Fact]
    public async Task UpsertChurchAsync_ExistingChurchConnectionClosed_OpensAndUpdates()
    {
        // Arrange — lookup returns an existing ChurchId, so the row is updated; connection starts Closed
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(Guid.CreateVersion7(DateTimeOffset.UtcNow)));
        var (worker, _) = BuildWorker(new FakeHttpClientFactory(), connection);

        // Act
        await worker.UpsertChurchAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — connection was opened; lookup + UPDATE executed (no INSERT/link)
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.Contains("UPDATE [dbo].[Churches]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertChurchAsync_NewChurchConnectionOpen_InsertsAndLinks()
    {
        // Arrange — lookup returns null (no existing ChurchId) so the row is new; connection already Open
        var connection = new FakeDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        var (worker, _) = BuildWorker(new FakeHttpClientFactory(), connection);

        // Act
        await worker.UpsertChurchAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — lookup + INSERT into Churches + link UPDATE on CrawlSources
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO [dbo].[Churches]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
        Assert.Contains("UPDATE [dbo].[CrawlSources]", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertChurchAsync_NewChurchNullOptionals_BindsDbNull()
    {
        // Arrange — null strings and null nullable-bools must all coalesce to DBNull
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(new FakeHttpClientFactory(), connection);
        var req = FullRequest with
        {
            CanonicalName = null,
            AcceptsLGBTQ = null,
            WheelchairAccessible = null,
            HasNursery = null,
            HasYouthProgram = null
        };

        // Act
        await worker.UpsertChurchAsync(req, 0m, 0m, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands[1];
        Assert.Equal(DBNull.Value, insert.Parameters["@Name"].Value);
        Assert.Equal(DBNull.Value, insert.Parameters["@Lgbtq"].Value);
        Assert.Equal(DBNull.Value, insert.Parameters["@Youth"].Value);
    }

    [Fact]
    public async Task UpsertChurchAsync_NewChurchPopulatedOptionals_BindsValues()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(new FakeHttpClientFactory(), connection);

        // Act
        await worker.UpsertChurchAsync(FullRequest, 33.4484m, -112.0740m, TestContext.Current.CancellationToken);

        // Assert — populated optionals bind their values; slug joins name-city-state
        var insert = connection.ExecutedCommands[1];
        Assert.Equal("Grace Church", insert.Parameters["@Name"].Value);
        Assert.Equal("grace-church-phoenix-az", insert.Parameters["@Slug"].Value);
        Assert.True(insert.Parameters["@Lgbtq"].Value is true);
        Assert.True(insert.Parameters["@Youth"].Value is false);
    }

    // --- Run orchestration ---
    [Fact]
    public async Task Run_NullPayload_CompletesWithoutDb()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, _) = BuildWorker(new FakeHttpClientFactory(), connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — no DB interaction; message completed
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
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

        // Assert — DB commands issued; geocoded lat/lng bound in INSERT
        Assert.NotEmpty(connection.ExecutedCommands);
        var insert = connection.ExecutedCommands[1];
        Assert.Equal(33.4484m, insert.Parameters["@Lat"].Value);
        Assert.Equal(-112.0740m, insert.Parameters["@Lng"].Value);
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
        return (new GeocoderWorker(factory, connection, config), connection);
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
