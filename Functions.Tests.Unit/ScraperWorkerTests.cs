namespace Functions.Tests.Unit;

using System.Net;
using System.Text;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Churches;
using Churches.Crawling;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Moq;
using static TestSupport.StubHttpMessageHandler;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ScraperWorkerTests
{
    private const string NonIanaCharsetContentType = "text/html; charset=utf8mb4";

    [Fact]
    public async Task Run_WhenPayloadIsNull_DeadLettersMessage()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, Returns(new HttpResponseMessage(HttpStatusCode.OK)));
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(
            a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WhenResponseNotSuccess_MarksFailedAndCompletes()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, Returns(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var message = BuildScrapeMessage();
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Contains("UPDATE [dbo].[CrawlSources]", update.CommandText, StringComparison.Ordinal);
        Assert.Equal(CrawlStatuses.Failed, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenResponseSuccess_StoresBlobQueuesExtractionAndCompletes()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NewHtmlDocument()) };
        var (worker, sender, blob) = BuildWorker(connection, Returns(response));
        var message = BuildScrapeMessage();
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(CrawlStatuses.Succeeded, update.Parameters["@Status"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenResponseHasNonIanaCharset_DecodesRawBytesAndCompletes()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(NewHtmlDocument()));
        content.Headers.TryAddWithoutValidation("Content-Type", NonIanaCharsetContentType);
        Assert.Equal(NonIanaCharsetContentType, content.Headers.GetValues("Content-Type").Single());
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var (worker, sender, blob) = BuildWorker(connection, Returns(response));
        var message = BuildScrapeMessage();
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(CrawlStatuses.Succeeded, update.Parameters["@Status"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenHttpRequestFails_MarksFailedAndCompletes()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, Throws(new HttpRequestException(NewFailureMessage())));
        var message = BuildScrapeMessage();
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(CrawlStatuses.Failed, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenHttpTimesOut_MarksFailedAndCompletes()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(
            connection,
            Throws(new TaskCanceledException(NewFailureMessage(), new TimeoutException())));
        var message = BuildScrapeMessage();
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(CrawlStatuses.Failed, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenUnexpectedExceptionThrown_MarksFailedAbandonsAndRethrows()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var unexpectedFailureMessage = NewFailureMessage();
        var (worker, sender, blob) = BuildWorker(connection, Throws(new InvalidOperationException(unexpectedFailureMessage)));
        var message = BuildScrapeMessage();
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.Run(message, actions.Object, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(unexpectedFailureMessage, thrown.Message);
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(CrawlStatuses.Failed, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenHostCancellationRequested_DoesNotCompleteMessage()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(
            connection,
            Throws(new TaskCanceledException(NewFailureMessage(), new TimeoutException())));
        var message = BuildScrapeMessage();
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.Run(message, actions.Object, cts.Token));

        // Assert
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ServiceBusReceivedMessage BuildScrapeMessage()
    {
        var crawlSourceId = Guid.NewGuid();
        var churchUrl = NewChurchUrl();
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(new ScrapeRequest(crawlSourceId, churchUrl)));
    }

    private static string NewChurchUrl() => TestValues.NewWebsite();

    private static string NewHtmlDocument() => $"<html><h1>{Guid.NewGuid():N}</h1></html>";

    private static string NewFailureMessage() => TestValues.NewErrorMessage();

    private static (ScraperWorker Worker, Mock<ServiceBusSender> Sender, Mock<BlobClient> Blob) BuildWorker(
        FakeDbConnection connection,
        HttpMessageHandler handler)
    {
        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var blobClient = new Mock<BlobClient>(MockBehavior.Strict);
        blobClient
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blobClient.Object);
        var blobService = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobService.Setup(s => s.GetBlobContainerClient(BlobContainerNames.Churches)).Returns(container.Object);
        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(blobService.Object);

        var sender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var bus = new Mock<ServiceBusClient>(MockBehavior.Strict);
        bus.Setup(c => c.CreateSender(ChurchQueueNames.ExtractionRequests)).Returns(sender.Object);
        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(bus.Object);

        var worker = new ScraperWorker(connection, blobFactory.Object, busFactory.Object, httpFactory.Object);
        return (worker, sender, blobClient);
    }
}
