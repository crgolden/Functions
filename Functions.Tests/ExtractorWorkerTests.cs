namespace Functions.Tests;

using System.Data;
using AngleSharp;
using AngleSharp.Dom;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ExtractorWorkerTests
{
    private const string FullMicrodataHtml = """
        <span itemprop="name">Grace Church</span>
        <span itemprop="addressLocality">Phoenix</span>
        <span itemprop="addressRegion">AZ</span>
        <span itemprop="postalCode">85001</span>
        <span itemprop="telephone">602-555-1212</span>
        """;

    // --- ExtractPhone (pure, internal static) ---
    [Fact]
    public async Task ExtractPhone_ItempropTelephonePresent_ReturnsItempropValue()
    {
        // Arrange
        var doc = await ParseHtmlAsync("<span itemprop=\"telephone\">  (602) 555-1212  </span><p>(480) 555-9999</p>");

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Equal("(602) 555-1212", phone);
    }

    [Fact]
    public async Task ExtractPhone_NoItempropButBodyHasMatch_ReturnsRegexMatch()
    {
        // Arrange
        var doc = await ParseHtmlAsync("<p>Call us at 602-555-1212 today.</p>");

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Equal("602-555-1212", phone);
    }

    [Fact]
    public async Task ExtractPhone_NoItempropNoMatch_ReturnsNull()
    {
        // Arrange
        var doc = await ParseHtmlAsync("<p>No phone number here.</p>");

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Null(phone);
    }

    // --- ExtractFromHtmlAsync (pure, internal static; AngleSharp in-memory) ---
    [Fact]
    public async Task ExtractFromHtmlAsync_FullMicrodata_ScoresHighWithItempropName()
    {
        // Arrange
        const string html = """
            <span itemprop="name">Grace Church</span>
            <span itemprop="addressLocality">Phoenix</span>
            <span itemprop="addressRegion">AZ</span>
            <span itemprop="postalCode">85001</span>
            <span itemprop="telephone">602-555-1212</span>
            """;

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(html, "https://grace.example");

        // Assert
        Assert.Equal("Grace Church", result.CanonicalName);
        Assert.Equal("Phoenix", result.City);
        Assert.Equal("AZ", result.State);
        Assert.Equal("85001", result.Zip);
        Assert.Equal(0.9m, result.Confidence);
        Assert.Equal("https://grace.example", result.Website);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoItempropNameButH1Present_NameFromH1()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<h1>St. Marks</h1>", "https://x.example");

        // Assert
        Assert.Equal("St. Marks", result.CanonicalName);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoItempropNoH1ButTitlePresent_NameFromTitle()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<html><head><title>Trinity</title></head><body><p>x</p></body></html>", "https://x.example");

        // Assert
        Assert.Equal("Trinity", result.CanonicalName);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoNameSource_NameIsBlankAndNotScored()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<span itemprop=\"addressLocality\">Phoenix</span>", "https://x.example");

        // Assert — only city contributes (0.2); name adds nothing
        Assert.Equal("Phoenix", result.City);
        Assert.Equal(0.2m, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_CityOnly_AddsCityScore()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<span itemprop=\"addressLocality\">Phoenix</span>", "https://x.example");

        // Assert
        Assert.Equal(0.2m, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_StateOnly_AddsStateScore()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<span itemprop=\"addressRegion\">AZ</span>", "https://x.example");

        // Assert
        Assert.Equal("AZ", result.State);
        Assert.Equal(0.2m, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_ZipOnly_AddsZipScore()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<span itemprop=\"postalCode\">85001</span>", "https://x.example");

        // Assert
        Assert.Equal("85001", result.Zip);
        Assert.Equal(0.2m, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_PhoneOnlyNoEmail_AddsContactScore()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<span itemprop=\"telephone\">602-555-1212</span>", "https://x.example");

        // Assert — contact OR: phone present satisfies the +0.1
        Assert.Equal("602-555-1212", result.PhoneNumber);
        Assert.Equal(0.1m, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_EmailOnlyNoPhone_AddsContactScore()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<a href=\"mailto:hello@grace.example\">email</a>", "https://x.example");

        // Assert — contact OR: email present (phone absent) still satisfies +0.1
        Assert.Equal("hello@grace.example", result.EmailAddress);
        Assert.Equal(0.1m, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NeitherPhoneNorEmail_NoContactScore()
    {
        // Arrange
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<span itemprop=\"addressLocality\">Phoenix</span>", "https://x.example");

        // Assert — no phone/email, so the +0.1 contact bonus is absent (only city's 0.2)
        Assert.Null(result.PhoneNumber);
        Assert.Null(result.EmailAddress);
        Assert.Equal(0.2m, result.Confidence);
    }

    // --- Run orchestration (blob-free paths) ---
    [Fact]
    public async Task Run_PayloadIsNull_CompletesWithoutDbAccess()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_BlankBlobPath_CompletesWithoutExtraction()
    {
        // Arrange — a blank BlobPath short-circuits DownloadBlobAsync to null before any blob call
        var connection = new FakeDbConnection();
        var worker = BuildWorker(connection);
        var payload = new ExtractionRequest(Guid.NewGuid(), string.Empty, "https://x.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — no DB upsert occurred; the message was completed
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Run orchestration (blob present; download/dispatch paths) ---
    [Fact]
    public async Task Run_BlobNotFound_CompletesWithoutExtraction()
    {
        // Arrange — ExistsAsync returns false, so DownloadBlobAsync yields null before any extraction
        var connection = new FakeDbConnection();
        var (worker, sender) = BuildWorkerWithHtml(connection, html: null);
        var payload = new ExtractionRequest(Guid.NewGuid(), "az/missing.html", "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — no extraction, no DB write, no enrichment; message completed
        Assert.Empty(connection.ExecutedCommands);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_HighConfidenceWithCity_UpsertsChurch()
    {
        // Arrange — full microdata scores 0.9 with a city, so it dispatches to the DB upsert
        var connection = new FakeDbConnection();
        var (worker, sender) = BuildWorkerWithHtml(connection, FullMicrodataHtml);
        var payload = new ExtractionRequest(Guid.NewGuid(), "az/grace.html", "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — DB upsert ran; no enrichment message sent; message completed
        Assert.NotEmpty(connection.ExecutedCommands);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_LowConfidence_SendsEnrichmentRequest()
    {
        // Arrange — only an <h1> scores 0.2 (< 0.5), so it routes to the enrichment queue
        var connection = new FakeDbConnection();
        var (worker, sender) = BuildWorkerWithHtml(connection, "<h1>Grace Church</h1>");
        var payload = new ExtractionRequest(Guid.NewGuid(), "az/grace.html", "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — no DB write; one enrichment message; message completed
        Assert.Empty(connection.ExecutedCommands);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_HighConfidenceButNoCity_SendsEnrichmentRequest()
    {
        // Arrange — name+state+zip+phone score 0.7 (>= 0.5) but city is absent, isolating the city condition
        const string html = """
            <h1>Grace Church</h1>
            <span itemprop="addressRegion">AZ</span>
            <span itemprop="postalCode">85001</span>
            <span itemprop="telephone">602-555-1212</span>
            """;
        var connection = new FakeDbConnection();
        var (worker, sender) = BuildWorkerWithHtml(connection, html);
        var payload = new ExtractionRequest(Guid.NewGuid(), "az/grace.html", "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — confidence cleared the threshold but the missing city forced enrichment
        Assert.Empty(connection.ExecutedCommands);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- UpsertChurchAsync (internal instance; FakeDbConnection) ---
    [Fact]
    public async Task UpsertChurchAsync_ExistingChurchConnectionClosed_OpensAndUpdates()
    {
        // Arrange — lookup returns an existing ChurchId, so the row is updated; connection starts Closed
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(Guid.CreateVersion7(DateTimeOffset.UtcNow)));
        var worker = BuildWorker(connection);

        // Act
        await worker.UpsertChurchAsync(Guid.NewGuid(), PopulatedResult(), TestContext.Current.CancellationToken);

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
        var worker = BuildWorker(connection);

        // Act
        await worker.UpsertChurchAsync(Guid.NewGuid(), PopulatedResult(), TestContext.Current.CancellationToken);

        // Assert — lookup + INSERT into Churches + link UPDATE on CrawlSources
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO [dbo].[Churches]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
        Assert.Contains("UPDATE [dbo].[CrawlSources]", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertChurchAsync_NewChurchNullOptionals_BindsDbNull()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(connection);

        // Act
        await worker.UpsertChurchAsync(Guid.NewGuid(), NullResult(), TestContext.Current.CancellationToken);

        // Assert — every nullable column coalesces to DBNull; slug is "--" with all parts blank
        var insert = connection.ExecutedCommands[1];
        Assert.Equal(DBNull.Value, insert.Parameters["@Name"].Value);
        Assert.Equal(DBNull.Value, insert.Parameters["@City"].Value);
        Assert.Equal(DBNull.Value, insert.Parameters["@Email"].Value);
        Assert.Equal("--", insert.Parameters["@Slug"].Value);
    }

    [Fact]
    public async Task UpsertChurchAsync_NewChurchPopulatedOptionals_BindsValuesAndSlug()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(connection);

        // Act
        await worker.UpsertChurchAsync(Guid.NewGuid(), PopulatedResult(), TestContext.Current.CancellationToken);

        // Assert — populated optionals bind their values; slug joins name-city-state
        var insert = connection.ExecutedCommands[1];
        Assert.Equal("Grace Church", insert.Parameters["@Name"].Value);
        Assert.Equal("grace-church-phoenix-az", insert.Parameters["@Slug"].Value);
    }

    private static ExtractionResult PopulatedResult() =>
        new(
            CanonicalName: "Grace Church",
            Street: "123 Main St",
            City: "Phoenix",
            State: "AZ",
            Zip: "85001",
            PhoneNumber: "602-555-1212",
            Website: "https://grace.example",
            EmailAddress: "hi@grace.example",
            Confidence: 0.9m);

    private static ExtractionResult NullResult() =>
        new(null, null, null, null, null, null, null, null, 0m);

    private static async Task<IDocument> ParseHtmlAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html));
    }

    private static (ExtractorWorker Worker, Mock<ServiceBusSender> Sender) BuildWorkerWithHtml(FakeDbConnection connection, string? html)
    {
        var response = Mock.Of<Response>();

        var blobClient = new Mock<BlobClient>(MockBehavior.Strict);
        blobClient
            .Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(html is not null, response));
        if (html is not null)
        {
            blobClient
                .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(
                    BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString(html)),
                    response));
        }

        var containerClient = new Mock<BlobContainerClient>(MockBehavior.Strict);
        containerClient.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blobClient.Object);

        var blobServiceClient = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobServiceClient.Setup(s => s.GetBlobContainerClient("churches")).Returns(containerClient.Object);

        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient("crgolden")).Returns(blobServiceClient.Object);

        var sender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender("enrichment-requests")).Returns(sender.Object);

        var serviceBusFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        serviceBusFactory.Setup(f => f.CreateClient("crgolden")).Returns(serviceBusClient.Object);

        return (new ExtractorWorker(connection, blobFactory.Object, serviceBusFactory.Object), sender);
    }

    private static ExtractorWorker BuildWorker(FakeDbConnection connection)
    {
        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient("crgolden")).Returns(new Mock<BlobServiceClient>(MockBehavior.Strict).Object);
        var serviceBusFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        serviceBusFactory.Setup(f => f.CreateClient("crgolden")).Returns(new Mock<ServiceBusClient>(MockBehavior.Strict).Object);
        return new ExtractorWorker(connection, blobFactory.Object, serviceBusFactory.Object);
    }
}
