namespace Functions.Tests.Unit;

using System.Globalization;
using System.Text.Json;
using Churches;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class NormalizerTests
{
    [Theory]
    [InlineData("({0}) {1}-{2}")]
    [InlineData("{0}-{1}-{2}")]
    [InlineData("{0}{1}{2}")]
    [InlineData("+1{0}{1}{2}")]
    [InlineData("1-{0}-{1}-{2}")]
    [InlineData("1{0}{1}{2}")]
    public void NormalizePhone_ValidFormats_ReturnsE164(string phoneFormat)
    {
        // Arrange
        var areaCode = Random.Shared.Next(200, 1000);
        var exchange = Random.Shared.Next(200, 1000);
        var lineNumber = Random.Shared.Next(1000, 10000);
        var formattedPhone = string.Format(CultureInfo.InvariantCulture, phoneFormat, areaCode, exchange, lineNumber);

        // Act
        var normalized = Normalizer.NormalizePhone(formattedPhone);

        // Assert
        Assert.Equal($"+1{areaCode}{exchange}{lineNumber}", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]
    [InlineData("12345678901234")]
    public void NormalizePhone_InvalidOrMissing_ReturnsNull(string? input)
    {
        Assert.Null(Normalizer.NormalizePhone(input));
    }

    [Theory]
    [InlineData("{0}{1}")]
    [InlineData("{0}{1}-{2}")]
    [InlineData("{0} {1}")]
    [InlineData("{0}{1}-")]
    public void NormalizeZip_ValidFormats_ReturnsFiveDigits(string zipFormat)
    {
        // Arrange
        var zipPrefix = Random.Shared.Next(10, 100);
        var zipSuffix = Random.Shared.Next(100, 1000);
        var plusFour = Random.Shared.Next(1000, 10000);
        var formattedZip = string.Format(CultureInfo.InvariantCulture, zipFormat, zipPrefix, zipSuffix, plusFour);

        // Act
        var normalized = Normalizer.NormalizeZip(formattedZip);

        // Assert
        Assert.Equal($"{zipPrefix}{zipSuffix}", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234")]
    public void NormalizeZip_InvalidOrMissing_ReturnsNull(string? input)
    {
        Assert.Null(Normalizer.NormalizeZip(input));
    }

    [Theory]
    [InlineData("https://{0}")]
    [InlineData("https://{0}/")]
    [InlineData("http://{0}")]
    [InlineData("http://{0}/")]
    [InlineData("{0}")]
    [InlineData("  {0}/  ")]
    [InlineData("https://{0};http://{1}")]
    [InlineData("{0};{1}")]
    public void NormalizeUrl_VariousSchemes_ReturnsHttpsWithoutTrailingSlash(string urlFormat)
    {
        // Arrange
        var primaryHost = TestValues.NewHost();
        var secondaryHost = TestValues.NewHost();
        var formattedUrl = string.Format(CultureInfo.InvariantCulture, urlFormat, primaryHost, secondaryHost);

        // Act
        var normalized = Normalizer.NormalizeUrl(formattedUrl);

        // Assert
        Assert.Equal($"https://{primaryHost}", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeUrl_NullOrWhitespace_ReturnsNull(string? input)
    {
        Assert.Null(Normalizer.NormalizeUrl(input));
    }

    [Theory]
    [InlineData("CO", "CO")]
    [InlineData("co", "CO")]
    [InlineData("Ohio", "OH")]
    [InlineData("alaska", "AK")]
    [InlineData("W. Va.", "WV")]
    [InlineData("-IL", "IL")]
    public void NormalizeState_RecognizedFormats_ReturnsTwoLetterCode(string input, string expected)
    {
        Assert.Equal(expected, Normalizer.NormalizeState(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeState_MissingOrBlank_ReturnsNull(string? input)
    {
        Assert.Null(Normalizer.NormalizeState(input));
    }

    [Fact]
    public void NormalizeState_UnrecognizedName_ReturnsNull()
    {
        // Arrange
        var unrecognizedState = $"State{Guid.NewGuid():N}";

        // Act
        var normalized = Normalizer.NormalizeState(unrecognizedState);

        // Assert
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeBlank_NullOrWhitespace_ReturnsNull(string? input)
    {
        Assert.Null(Normalizer.NormalizeBlank(input));
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("  {0}  ")]
    public void NormalizeBlank_NonBlank_ReturnsTrimmedValue(string valueFormat)
    {
        // Arrange
        var cityName = TestValues.NewCity();
        var formattedValue = string.Format(CultureInfo.InvariantCulture, valueFormat, cityName);

        // Act
        var normalized = Normalizer.NormalizeBlank(formattedValue);

        // Assert
        Assert.Equal(cityName, normalized);
    }

    [Fact]
    public void GetJsonString_MissingProperty_ReturnsNull()
    {
        // Arrange
        var propertyName = NewPropertyName();
        using var doc = JsonDocument.Parse(JsonObject(new Dictionary<string, object>()));

        // Act
        var value = Normalizer.GetJsonString(doc.RootElement, propertyName);

        // Assert
        Assert.Null(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetJsonString_BlankStringValue_ReturnsNull(string blankValue)
    {
        // Arrange
        var propertyName = NewPropertyName();
        using var doc = JsonDocument.Parse(JsonObject(new Dictionary<string, object> { [propertyName] = blankValue }));

        // Act
        var value = Normalizer.GetJsonString(doc.RootElement, propertyName);

        // Assert
        Assert.Null(value);
    }

    [Fact]
    public void GetJsonString_NonStringValue_ReturnsNull()
    {
        // Arrange
        var propertyName = NewPropertyName();
        var numericValue = Random.Shared.Next(1, 1000);
        using var doc = JsonDocument.Parse(JsonObject(new Dictionary<string, object> { [propertyName] = numericValue }));

        // Act
        var value = Normalizer.GetJsonString(doc.RootElement, propertyName);

        // Assert
        Assert.Null(value);
    }

    [Fact]
    public void GetJsonString_NonBlankStringValue_ReturnsValue()
    {
        // Arrange
        var propertyName = NewPropertyName();
        var cityName = TestValues.NewCity();
        using var doc = JsonDocument.Parse(JsonObject(new Dictionary<string, object> { [propertyName] = cityName }));

        // Act
        var value = Normalizer.GetJsonString(doc.RootElement, propertyName);

        // Assert
        Assert.Equal(cityName, value);
    }

    private static string NewPropertyName() => $"property{Guid.NewGuid():N}";

    private static string JsonObject(Dictionary<string, object> properties) =>
        JsonSerializer.Serialize(properties);
}
