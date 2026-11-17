namespace Functions.Extensions;

using System.Text.Json;
using Azure.Messaging.ServiceBus;

public static class ServiceBusReceivedMessageExtensions
{
    extension(ServiceBusReceivedMessage message)
    {
        public T? ReadJsonPayloadOrNull<T>()
            where T : class
        {
            try
            {
                return message.Body.ToObjectFromJson<T>();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
