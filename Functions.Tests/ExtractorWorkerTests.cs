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
    public async Task Run_PayloadIsNull_CompletesWithoutSendingAnything()
    {
        // Arrange
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html: null);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_BlankBlobPath_CompletesWithoutExtraction()
    {
        // Arrange
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html: null);
        var payload = new ExtractionRequest(Guid.NewGuid(), string.Empty, "https://x.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Run orchestration (blob present; dispatch paths) ---
    [Fact]
    public async Task Run_BlobNotFound_CompletesWithoutSendingAnything()
    {
        // Arrange
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html: null);
        var payload = new ExtractionRequest(Guid.NewGuid(), "az/missing.html", "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_HighConfidenceWithCity_SendsGeocodingRequest()
    {
        // Arrange — full microdata scores 0.9 with a city, so it routes to geocoding-requests
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(FullMicrodataHtml);
        var payload = new ExtractionRequest(Guid.NewGuid(), "az/grace.html", "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — geocoding message sent; no enrichment message; message completed
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_LowConfidence_SendsEnrichmentRequest()
    {
        // Arrange — only an <h1> scores 0.2 (< 0.5), so it routes to the enrichment queue
        var (worker, geocodingSender, enrichmentSender) = BuildWorker("<h1>Grace Church</h1>");
        var payload = new ExtractionRequest(Guid.NewGuid(), "az/grace.html", "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — no geocoding message; one enrichment message; message completed
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_HighConfidenceButNoCity_SendsEnrichmentRequest()
    {
        // Arrange — name+state+zip+phone score 0.7 (>= 0.5) but city is absent
        const string html = """
            <h1>Grace Church</h1>
            <span itemprop="addressRegion">AZ</span>
            <span itemprop="postalCode">85001</span>
            <span itemprop="telephone">602-555-1212</span>
            """;
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html);
        var payload = new ExtractionRequest(Guid.NewGuid(), "az/grace.html", "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — confidence cleared the threshold but the missing city forced enrichment
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<IDocument> ParseHtmlAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html));
    }

    private static (ExtractorWorker Worker, Mock<ServiceBusSender> GeocodingSender, Mock<ServiceBusSender> EnrichmentSender) BuildWorker(string? html)
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

        var geocodingSender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        geocodingSender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        geocodingSender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var enrichmentSender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        enrichmentSender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        enrichmentSender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender("geocoding-requests")).Returns(geocodingSender.Object);
        serviceBusClient.Setup(c => c.CreateSender("enrichment-requests")).Returns(enrichmentSender.Object);

        var serviceBusFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        serviceBusFactory.Setup(f => f.CreateClient("crgolden")).Returns(serviceBusClient.Object);

        return (new ExtractorWorker(blobFactory.Object, serviceBusFactory.Object), geocodingSender, enrichmentSender);
    }
}
