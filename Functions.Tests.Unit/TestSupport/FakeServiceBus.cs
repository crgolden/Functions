namespace Functions.Tests.Unit.TestSupport;

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;
using Moq;

internal static class FakeServiceBus
{
    public static (IAzureClientFactory<ServiceBusClient> Factory, List<ServiceBusMessage> Sent) Create()
    {
        var sent = new List<ServiceBusMessage>();
        var sender = new Mock<ServiceBusSender>();
        sender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Returns((ServiceBusMessage m, CancellationToken _) =>
            {
                sent.Add(m);
                return Task.CompletedTask;
            });
        sender
            .Setup(s => s.SendMessagesAsync(It.IsAny<IEnumerable<ServiceBusMessage>>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ServiceBusMessage> messages, CancellationToken _) =>
            {
                sent.AddRange(messages);
                return Task.CompletedTask;
            });
        sender
            .Setup(s => s.ScheduleMessageAsync(
                It.IsAny<ServiceBusMessage>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((ServiceBusMessage m, DateTimeOffset enqueueTime, CancellationToken _) =>
            {
                m.ScheduledEnqueueTime = enqueueTime;
                sent.Add(m);
                return Task.FromResult((long)sent.Count);
            });
        sender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var client = new Mock<ServiceBusClient>();
        client.Setup(c => c.CreateSender(It.IsAny<string>())).Returns(sender.Object);

        var factory = new Mock<IAzureClientFactory<ServiceBusClient>>();
        factory.Setup(f => f.CreateClient("crgolden")).Returns(client.Object);

        return (factory.Object, sent);
    }
}