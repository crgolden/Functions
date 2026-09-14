namespace Functions.Tests.Unit;

using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Churches;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class QueueDepthMonitorJobTests
{
    [Fact]
    public async Task Run_WhenAdminClientThrowsRequestFailedException_HandlesGracefullyForEveryQueue()
    {
        // Arrange
        var failureStatus = Random.Shared.Next(400, 600);
        var failureMessage = TestValues.NewErrorMessage();
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

    [Fact]
    public async Task Run_WhenInvocationArrivesAlreadyCancelled_ReadsNoQueue()
    {
        // Arrange
        var adminClient = new Mock<ServiceBusAdministrationClient>(MockBehavior.Strict);
        var factory = new Mock<IAzureClientFactory<ServiceBusAdministrationClient>>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(adminClient.Object);
        var job = new QueueDepthMonitorJob(factory.Object);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        await job.Run(new TimerInfo(), cancellation.Token);

        // Assert
        adminClient.Verify(
            c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_WhenCancellationArrivesMidCycle_StopsWithoutReadingLaterQueues()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Action cancelTheCycle = cancellation.Cancel;
        var adminClient = new Mock<ServiceBusAdministrationClient>(MockBehavior.Strict);
        adminClient
            .Setup(c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                cancelTheCycle();
                return Task.FromException<Response<QueueRuntimeProperties>>(new TaskCanceledException());
            });
        var factory = new Mock<IAzureClientFactory<ServiceBusAdministrationClient>>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(adminClient.Object);
        var job = new QueueDepthMonitorJob(factory.Object);

        // Act
        await job.Run(new TimerInfo(), cancellation.Token);

        // Assert
        adminClient.Verify(
            c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WhenCallIsCancelledWithoutHostCancellation_Throws()
    {
        // Arrange
        var adminClient = new Mock<ServiceBusAdministrationClient>(MockBehavior.Strict);
        adminClient
            .Setup(c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());
        var factory = new Mock<IAzureClientFactory<ServiceBusAdministrationClient>>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(adminClient.Object);
        var job = new QueueDepthMonitorJob(factory.Object);

        // Act
        var run = () => job.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<TaskCanceledException>(run);
    }
}
