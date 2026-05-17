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
    }
}
