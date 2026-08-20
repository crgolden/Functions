namespace Functions.Tests.Unit;

using Extensions;
using Microsoft.Extensions.Configuration;

[Trait("Category", "Unit")]
public sealed class ConfigurationExtensionsTests
{
    [Fact]
    public void GetRequired_ReturnsValue_WhenKeyExists()
    {
        // Arrange
        var configuredKey = NewSettingKey();
        var configuredValue = $"value{Guid.NewGuid():N}";
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [configuredKey] = configuredValue })
            .Build();

        // Act
        var resolved = config.GetRequired<string>(configuredKey);

        // Assert
        Assert.Equal(configuredValue, resolved);
    }

    [Fact]
    public void GetRequired_ThrowsInvalidOperationExceptionWithKeyName_WhenKeyMissing()
    {
        // Arrange
        var missingKey = NewSettingKey();
        IConfiguration config = new ConfigurationBuilder().Build();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => config.GetRequired<string>(missingKey));

        // Assert
        Assert.Equal($"Invalid '{missingKey}'.", exception.Message);
    }

    private static string NewSettingKey() => $"Setting{Guid.NewGuid():N}";
}
