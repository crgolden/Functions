namespace Functions.Tests;

using System.Net;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class BulkImportJobTests
{
    // --- ParseIrsCsv (pure, internal static) ---
    [Fact]
    public void ParseIrsCsv_SingleRow_MapsNameStreetCityStateZip()
    {
        // Arrange
        const string csv = """
            NAME,STREET,CITY,STATE,ZIP,NTEE_CD
            Grace Church,"123 Main St",Phoenix,AZ,85001,X20
            """;

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Single(results);
        var r = results[0];
        Assert.Equal("Grace Church", r.CanonicalName);
        Assert.Equal("123 Main St", r.Street);
        Assert.Equal("Phoenix", r.City);
        Assert.Equal("AZ", r.State);
        Assert.Equal("85001", r.Zip);
        Assert.Equal(0, r.WorshipStyle);
        Assert.Equal(0.5m, r.Confidence);
    }

    [Fact]
    public void ParseIrsCsv_NteeX21_MapsToLiturgical()
    {
        // Arrange
        const string csv = "NAME,STATE,NTEE_CD\nSt. Anthony's,AZ,X21";

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(5, results[0].WorshipStyle);
    }

    [Fact]
    public void ParseIrsCsv_NteeX22_MapsToLiturgical()
    {
        // Arrange
        const string csv = "NAME,STATE,NTEE_CD\nSt. George,AZ,X22";

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(5, results[0].WorshipStyle);
    }

    [Fact]
    public void ParseIrsCsv_MissingNameColumn_SkipsRow()
    {
        // Arrange — row has no name so it is skipped
        const string csv = "NAME,STATE\n,AZ";

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_MissingStateColumn_SkipsRow()
    {
        // Arrange
        const string csv = "NAME,STATE\nGrace Church,";

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_EmptyCsv_YieldsNothing()
    {
        // Act
        var results = BulkImportJob.ParseIrsCsv(string.Empty).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_HeaderOnly_YieldsNothing()
    {
        // Act
        var results = BulkImportJob.ParseIrsCsv("NAME,STATE,NTEE_CD").ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseIrsCsv_MultipleRows_ParsesAll()
    {
        // Arrange
        const string csv = """
            NAME,STATE,NTEE_CD
            Grace Church,AZ,X20
            Trinity Church,CO,X21
            """;

        // Act
        var results = BulkImportJob.ParseIrsCsv(csv).ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("Grace Church", results[0].CanonicalName);
        Assert.Equal("Trinity Church", results[1].CanonicalName);
    }

    // --- ParseOsm (pure, internal static) ---
    [Fact]
    public void ParseOsm_SingleElement_MapsAllAddressFields()
    {
        // Arrange
        const string json = """
            {"elements":[{"tags":{"name":"St. Mark's","addr:street":"456 Oak Ave","addr:city":"Denver","addr:state":"CO","addr:postcode":"80201","phone":"303-555-1234","website":"https://stmarks.example","email":"info@stmarks.example"}}]}
            """;

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Single(results);
        var r = results[0];
        Assert.Equal("St. Mark's", r.CanonicalName);
        Assert.Equal("456 Oak Ave", r.Street);
        Assert.Equal("Denver", r.City);
        Assert.Equal("CO", r.State);
        Assert.Equal("80201", r.Zip);
        Assert.Equal("303-555-1234", r.PhoneNumber);
        Assert.Equal("https://stmarks.example", r.Website);
        Assert.Equal("info@stmarks.example", r.EmailAddress);
        Assert.Equal(0.6m, r.Confidence);
    }

    [Fact]
    public void ParseOsm_ElementMissingName_SkipsRow()
    {
        // Arrange
        const string json = """{"elements":[{"tags":{"addr:state":"CO"}}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingState_SkipsRow()
    {
        // Arrange
        const string json = """{"elements":[{"tags":{"name":"Grace"}}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_ElementMissingTags_SkipsRow()
    {
        // Arrange
        const string json = """{"elements":[{"id":1}]}""";

        // Act
        var results = BulkImportJob.ParseOsm(json).ToList();

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void ParseOsm_NoElementsKey_YieldsNothing()
    {
        // Act
        var results = BulkImportJob.ParseOsm("{}").ToList();

        // Assert
        Assert.Empty(results);
    }

    // --- NteeToWorshipStyle ---
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("X20", 0)]
    [InlineData("X21", 5)]
    [InlineData("X22", 5)]
    [InlineData("X50", 0)]
    public void NteeToWorshipStyle_VariousCodes_ReturnsExpected(string? ntee, int expected)
    {
        Assert.Equal(expected, BulkImportJob.NteeToWorshipStyle(ntee));
    }

    // --- Run orchestration ---
    [Fact]
    public async Task Run_MissingBlobPath_ReturnsBadRequest()
    {
        // Arrange
        var (worker, _, _) = BuildWorker(new FakeDbConnection(), blobContent: null);
        var req = BuildRequest([]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Run_BlobNotFound_ReturnsNotFound()
    {
        // Arrange
        var (worker, _, _) = BuildWorker(new FakeDbConnection(), blobContent: null);
        var req = BuildRequest([new("blobPath", "missing.csv")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Run_NewIrsRecords_PublishesAndReturnsOk()
    {
        // Arrange — two records, DB returns no existing matches → both published
        const string csv = "NAME,STATE,NTEE_CD\nGrace Church,AZ,X20\nTrinity,CO,X20";
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));

        var (worker, sender, _) = BuildWorker(connection, blobContent: csv);
        var req = BuildRequest([new("blobPath", "irs/test.csv"), new("source", "irs")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert — both records published
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Run_DuplicateRecord_SkipsExistingInDb()
    {
        // Arrange — DB returns "1" for both lookups → both skipped
        const string csv = "NAME,STATE,NTEE_CD\nGrace Church,AZ,X20\nTrinity,CO,X20";
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));
        connection.Enqueue(FakeDbCommand.WithScalarResult(1));

        var (worker, sender, _) = BuildWorker(connection, blobContent: csv);
        var req = BuildRequest([new("blobPath", "irs/test.csv"), new("source", "irs")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert — no messages published; response still OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_OsmSource_ParsesOsmAndPublishes()
    {
        // Arrange
        const string json = """
            {"elements":[{"tags":{"name":"St. Mark's","addr:state":"CO"}}]}
            """;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));

        var (worker, sender, _) = BuildWorker(connection, blobContent: json);
        var req = BuildRequest([new("blobPath", "osm/co.json"), new("source", "osm")]);

        // Act
        var response = await worker.Run(req, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (BulkImportJob Worker, Mock<ServiceBusSender> Sender, FakeDbConnection Connection) BuildWorker(
        FakeDbConnection connection,
        string? blobContent)
    {
        var azureResponse = Mock.Of<Response>();

        var blobClient = new Mock<BlobClient>(MockBehavior.Strict);
        blobClient
            .Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(blobContent is not null, azureResponse));
        if (blobContent is not null)
        {
            blobClient
                .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(
                    BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString(blobContent)),
                    azureResponse));
        }

        var containerClient = new Mock<BlobContainerClient>(MockBehavior.Strict);
        containerClient.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blobClient.Object);

        var blobServiceClient = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobServiceClient.Setup(s => s.GetBlobContainerClient("imports")).Returns(containerClient.Object);

        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient("crgolden")).Returns(blobServiceClient.Object);

        var sender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender("geocoding-requests")).Returns(sender.Object);

        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient("crgolden")).Returns(serviceBusClient.Object);

        var worker = new BulkImportJob(blobFactory.Object, busFactory.Object, connection, NullLogger<BulkImportJob>.Instance);
        return (worker, sender, connection);
    }

    private static FakeHttpRequestData BuildRequest(IEnumerable<KeyValuePair<string, string>> query)
    {
        var queryCollection = new System.Collections.Specialized.NameValueCollection();
        foreach (var (key, value) in query)
        {
            queryCollection[key] = value;
        }

        return new FakeHttpRequestData(queryCollection);
    }
}
