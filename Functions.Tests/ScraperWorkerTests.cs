namespace Functions.Tests;

using System.Net;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Moq;
using TestSupport;
using static TestSupport.StubHttpMessageHandler;

[Trait("Category", "Unit")]
public sealed class ScraperWorkerTests
{
    [Fact]
    public async Task Run_WhenPayloadIsNull_CompletesWithoutHttp()
    {
        // Arrange — the handler must never be invoked
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)));
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenResponseNotSuccess_MarksFailedAndCompletes()
    {
        // Arrange — a 404 short-circuits before any blob/extraction work
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var payload = new ScrapeRequest(Guid.NewGuid(), "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — crawl status updated to failed (2); no blob upload, no extraction message
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Contains("UPDATE [dbo].[CrawlSources]", update.CommandText, StringComparison.Ordinal);
        Assert.Equal(2, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenResponseSuccess_StoresBlobQueuesExtractionAndCompletes()
    {
        // Arrange — a 200 drives the full happy path: store blob → send extraction-request → mark crawled (1)
        var connection = new FakeDbConnection();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><h1>Grace</h1></html>") };
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Returns(response));
        var payload = new ScrapeRequest(Guid.NewGuid(), "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(1, update.Parameters["@Status"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenHttpThrows_MarksFailedAndAbandons()
    {
        // Arrange — the HTTP call faults, so the catch marks failed (2) and abandons the message
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Throws(new HttpRequestException("boom")));
        var payload = new ScrapeRequest(Guid.NewGuid(), "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — crawl status failed (2), message abandoned, nothing queued
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(2, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (ScraperWorker Worker, Mock<ServiceBusSender> Sender, Mock<BlobClient> Blob) BuildWorker(
        FakeDbConnection connection,
        HttpMessageHandler handler)
    {
        var response = Mock.Of<Response>();

        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var blobClient = new Mock<BlobClient>(MockBehavior.Strict);
        blobClient
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<BlobContentInfo>(null!, response));
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blobClient.Object);
        var blobService = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobService.Setup(s => s.GetBlobContainerClient("churches")).Returns(container.Object);
        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient("crgolden")).Returns(blobService.Object);

        var sender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var bus = new Mock<ServiceBusClient>(MockBehavior.Strict);
        bus.Setup(c => c.CreateSender("extraction-requests")).Returns(sender.Object);
        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient("crgolden")).Returns(bus.Object);

        var worker = new ScraperWorker(connection, blobFactory.Object, busFactory.Object, httpFactory.Object);
        return (worker, sender, blobClient);
    }
}
