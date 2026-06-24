namespace Functions.Extensions;

using Microsoft.Extensions.Configuration;

public static class ConfigurationExtensions
{
    extension(IConfiguration configuration)
    {
        public T GetRequired<T>(string key)
            where T : notnull
        {
            return configuration.GetValue<T?>(key) ?? throw new InvalidOperationException($"Invalid '{key}'.");
        }

#pragma warning disable SA1009
        internal (
            string ResendApiToken,
            string ServiceBusConnectionString,
            string StorageConnectionString,
            string OpenAIApiKey
        ) GetFunctionsSecrets()
        {
            var resendApiToken = configuration.GetRequired<string>("ResendApiToken");
            var serviceBusConnectionString = configuration.GetRequired<string>("ServiceBusConnection");
            var storageConnectionString = configuration.GetRequired<string>("StorageConnectionString");
            var openAIApiKey = configuration.GetRequired<string>("OpenAIApiKey");
            return (
                resendApiToken,
                serviceBusConnectionString,
                storageConnectionString,
                openAIApiKey
            );
        }
#pragma warning restore SA1009
    }
}
