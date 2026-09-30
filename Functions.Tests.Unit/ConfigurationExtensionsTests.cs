namespace Functions.Tests.Unit;

using System.Globalization;
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

    [Fact]
    public void GetRequiredValues_SetIsConfigured_ReturnsTheIndexedValuesInOrder()
    {
        // Arrange
        var configuredKey = Generated.NewSettingKey();
        var firstValue = Generated.NewFieldValue();
        var secondValue = Generated.NewFieldValue();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ConfigurationPath.Combine(configuredKey, 0.ToString(CultureInfo.InvariantCulture))] = firstValue,
                [ConfigurationPath.Combine(configuredKey, 1.ToString(CultureInfo.InvariantCulture))] = secondValue,
            })
            .Build();

        // Act
        var resolved = config.GetRequiredValues(configuredKey);

        // Assert
        Assert.Equal([firstValue, secondValue], resolved);
    }

    [Fact]
    public void GetRequiredValues_SetIsMissing_ThrowsNamingTheKey()
    {
        // Arrange
        var missingKey = Generated.NewSettingKey();
        IConfiguration config = new ConfigurationBuilder().Build();

        // Act
        var exception = Record.Exception(() => config.GetRequiredValues(missingKey));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(missingKey, invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequiredValues_EveryConfiguredValueIsBlank_ThrowsNamingTheKey()
    {
        // Arrange
        var configuredKey = Generated.NewSettingKey();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ConfigurationPath.Combine(configuredKey, 0.ToString(CultureInfo.InvariantCulture))] = Generated.NewBlank(),
            })
            .Build();

        // Act
        var exception = Record.Exception(() => config.GetRequiredValues(configuredKey));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(configuredKey, invalid.Message, StringComparison.Ordinal);
    }
}
