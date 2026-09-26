namespace Functions.Tests.Unit;

using Functions.Extensions;
using Microsoft.Extensions.Configuration;

[Trait("Category", "Unit")]
public sealed class ConfigurationExtensionsTests
{
    [Fact]
    public void GetRequired_ReturnsValue_WhenKeyExists()
    {
        // Arrange
        var configuredKey = Generated.NewSettingKey();
        var configuredValue = Generated.NewFieldValue();
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
        var missingKey = Generated.NewSettingKey();
        IConfiguration config = new ConfigurationBuilder().Build();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => config.GetRequired<string>(missingKey));

        // Assert
        Assert.Contains(missingKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequired_ThrowsRatherThanReturningZero_WhenAnIntKeyIsMissing()
    {
        // Arrange
        var missingKey = Generated.NewSettingKey();
        IConfiguration config = new ConfigurationBuilder().Build();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => config.GetRequired<int>(missingKey));

        // Assert
        Assert.Contains(missingKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequired_ThrowsRatherThanReturningFalse_WhenABoolKeyIsMissing()
    {
        // Arrange
        var missingKey = Generated.NewSettingKey();
        IConfiguration config = new ConfigurationBuilder().Build();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => config.GetRequired<bool>(missingKey));

        // Assert
        Assert.Contains(missingKey, exception.Message, StringComparison.Ordinal);
    }
}
