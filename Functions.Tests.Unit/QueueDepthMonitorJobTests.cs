namespace Functions.Tests.Unit;

using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Azure;
using Moq;

[Trait("Category", "Unit")]
public sealed class QueueDepthMonitorJobTests
{
    [Fact]
    public async Task Run_WhenAdminClientThrowsRequestFailedException_HandlesGracefullyForEveryQueue()
    {
        // Arrange
        var adminClient = new Mock<ServiceBusAdministrationClient>(MockBehavior.Strict);
        adminClient
            .Setup(c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "Forbidden"));
        var factory = new Mock<IAzureClientFactory<ServiceBusAdministrationClient>>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient("crgolden")).Returns(adminClient.Object);
        var job = new QueueDepthMonitorJob(factory.Object);

        // Act
        await job.Run(timer: null!, TestContext.Current.CancellationToken);

        // Assert
        adminClient.Verify(c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeast(7));
    }
}