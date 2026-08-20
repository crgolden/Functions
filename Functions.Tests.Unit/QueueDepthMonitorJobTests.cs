namespace Functions.Tests.Unit;

using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Churches;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Moq;

[Trait("Category", "Unit")]
public sealed class QueueDepthMonitorJobTests
{
    [Fact]
    public async Task Run_WhenAdminClientThrowsRequestFailedException_HandlesGracefullyForEveryQueue()
    {
        // Arrange
        var failureStatus = Random.Shared.Next(400, 600);
        var failureMessage = $"failure{Guid.NewGuid():N}";
        var adminClient = new Mock<ServiceBusAdministrationClient>(MockBehavior.Strict);
        adminClient
            .Setup(c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(failureStatus, failureMessage));
        var factory = new Mock<IAzureClientFactory<ServiceBusAdministrationClient>>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(adminClient.Object);
        var job = new QueueDepthMonitorJob(factory.Object);

        // Act
        await job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        adminClient.Verify(
            c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(QueueDepthMonitorJob.QueueNames.Length));
    }
}
