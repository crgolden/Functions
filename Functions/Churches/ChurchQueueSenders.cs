namespace Functions.Churches;

using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;

public sealed class ChurchQueueSenders
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);

    public ChurchQueueSenders(IAzureClientFactory<ServiceBusClient> serviceBusClientFactory) =>
        _serviceBusClient = serviceBusClientFactory.CreateClient(AzureClientNames.Crgolden);

    public ServiceBusSender For(string queueName) =>
        _senders.GetOrAdd(queueName, _serviceBusClient.CreateSender);
}