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
        // Arrange — missing Manage claims (or any other admin-API failure) must not throw: that
        // would feed the exceptions alert every 15 minutes instead of just degrading to a trace event.
        var adminClient = new Mock<ServiceBusAdministrationClient>(MockBehavior.Strict);
        adminClient
            .Setup(c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "Forbidden"));
        var factory = new Mock<IAzureClientFactory<ServiceBusAdministrationClient>>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient("crgolden")).Returns(adminClient.Object);
        var job = new QueueDepthMonitorJob(factory.Object);

        // Act / Assert — no exception escapes Run despite every queue lookup failing
        await job.Run(timer: null!, TestContext.Current.CancellationToken);

        adminClient.Verify(c => c.GetQueueRuntimePropertiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeast(7));
    }
}
