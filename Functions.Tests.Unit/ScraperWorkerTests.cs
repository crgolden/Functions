namespace Functions.Tests.Unit;

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
    public async Task Run_WhenPayloadIsNull_DeadLettersMessage()
    {
        // Arrange — the handler must never be invoked
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.OK)));
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task Run_WhenHttpRequestFails_MarksFailedAndCompletes()
    {
        // Arrange — a dead site/DNS failure is an expected crawl outcome, same as a non-success
        // status code: mark the source failed and complete (no throw, no abandon, no retry storm).
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Throws(new HttpRequestException("boom")));
        var payload = new ScrapeRequest(Guid.NewGuid(), "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — crawl status failed (2), message completed, nothing queued, no throw
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(2, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenHttpTimesOut_MarksFailedAndCompletes()
    {
        // Arrange — HttpClient's 30s timeout surfaces as TaskCanceledException with an inner
        // TimeoutException; this is the same "expected failure" shape as HttpRequestException.
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Throws(new TaskCanceledException("timeout", new TimeoutException())));
        var payload = new ScrapeRequest(Guid.NewGuid(), "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(2, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenUnexpectedExceptionThrown_MarksFailedAbandonsAndRethrows()
    {
        // Arrange — an unexpected exception type is not treated as an expected fetch failure: mark
        // failed (2), abandon the message, and rethrow so the global exception-handling middleware
        // still records the failure.
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Throws(new InvalidOperationException("boom")));
        var payload = new ScrapeRequest(Guid.NewGuid(), "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.Run(message, actions.Object, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("boom", thrown.Message);
        var update = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(2, update.Parameters["@Status"].Value);
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenHostCancellationRequested_DoesNotCompleteMessage()
    {
        // Arrange — cancellation caused by host shutdown (the function's own token is already
        // cancelled) must NOT be treated as an expected fetch failure: the `when` filter's
        // !cancellationToken.IsCancellationRequested check excludes it, so it falls to the
        // catch-all instead of completing the message as "handled". Note: FakeDbConnection's
        // OpenAsync uses the real DbConnection base implementation, which honors a cancelled
        // token and faults immediately — so the catch-all's own DB update/abandon calls never
        // run either in this test, which is a fake-infra limit, not a claim about production
        // behavior. The one thing this test can prove is what it asserts: no expected-failure
        // "complete" path is taken. A strict mock with no Complete/Abandon setup means either
        // call would itself throw, causing the test to fail with a different exception type.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var connection = new FakeDbConnection();
        var (worker, sender, blob) = BuildWorker(connection, StubHttpMessageHandler.Throws(new TaskCanceledException("timeout", new TimeoutException())));
        var payload = new ScrapeRequest(Guid.NewGuid(), "https://grace.example");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.Run(message, actions.Object, cts.Token));

        // Assert
        sender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
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
