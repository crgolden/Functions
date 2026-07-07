namespace Functions.Tests.Unit;

using Functions.Extensions;
using Microsoft.Extensions.Configuration;

[Trait("Category", "Unit")]
public sealed class ConfigurationExtensionsTests
{
    [Fact]
    public void GetRequired_ReturnsValue_WhenKeyExists()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Foo"] = "bar" })
            .Build();

        Assert.Equal("bar", config.GetRequired<string>("Foo"));
    }

    [Fact]
    public void GetRequired_ThrowsInvalidOperationExceptionWithKeyName_WhenKeyMissing()
    {
        IConfiguration config = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => config.GetRequired<string>("Missing"));
        Assert.Equal("Invalid 'Missing'.", ex.Message);
    }

    [Fact]
    public void GetFunctionsSecrets_ReadsAllConfiguredKeys()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ResendApiToken"] = "resend-token",
                ["ServiceBusConnection"] = "sb-conn",
                ["StorageConnectionString"] = "storage-conn",
                ["OpenAIApiKey"] = "openai-key",
            })
            .Build();

        var secrets = config.GetFunctionsSecrets();

        Assert.Equal("resend-token", secrets.ResendApiToken);
        Assert.Equal("sb-conn", secrets.ServiceBusConnectionString);
        Assert.Equal("storage-conn", secrets.StorageConnectionString);
        Assert.Equal("openai-key", secrets.OpenAIApiKey);
    }
}
