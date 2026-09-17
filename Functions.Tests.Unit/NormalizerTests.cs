namespace Functions.Tests.Unit;

using System.Globalization;
using System.Text.Json;
using Churches;
using TestSupport;
using static NormalizerFixtureConstants;
using static TestSupport.TestValues;

[Trait("Category", "Unit")]
public sealed class NormalizerTests
{
    public static TheoryData<string> BlankValues() => [string.Empty, NewBlankRun()];

    public static TheoryData<string, string> RecognizedStateSpellings() => new()
    {
        { ColoradoCode, ColoradoCode },
        { ColoradoCode.ToLowerInvariant(), ColoradoCode },
        { OhioName, OhioCode },
        { LowercaseAlaskaName, AlaskaCode },
        { WestVirginiaInformalAbbreviation, WestVirginiaCode },
        { $"-{IllinoisCode}", IllinoisCode },
    };

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
        Assert.Equal($"{Normalizer.NorthAmericanE164Prefix}{areaCode}{exchange}{lineNumber}", normalized);
    }

    [Theory]
    [InlineData(null)]
    [MemberData(nameof(BlankValues))]
    public void NormalizePhone_MissingOrBlank_ReturnsNull(string? input)
    {
        // Act
        var normalized = Normalizer.NormalizePhone(input);

        // Assert
        Assert.Null(normalized);
    }

    [Fact]
    public void NorthAmericanE164Prefix_IsTheE164PlusAndTheNanpCountryCode()
    {
        // Act
        var prefix = Normalizer.NorthAmericanE164Prefix;

        // Assert
        Assert.Equal("+1", prefix);
    }

    [Fact]
    public void NormalizePhone_FewerDigitsThanANorthAmericanNumber_ReturnsNull()
    {
        // Arrange
        var tooFewDigitCount = Random.Shared.Next(1, NorthAmericanDigitCount);
        var tooFewDigits = DigitToken(tooFewDigitCount);

        // Act
        var normalized = Normalizer.NormalizePhone(tooFewDigits);

        // Assert
        Assert.Null(normalized);
    }

    [Fact]
    public void NormalizePhone_MoreDigitsThanACountryCodedNorthAmericanNumber_ReturnsNull()
    {
        // Arrange
        var tooManyDigitCount = Random.Shared.Next(NorthAmericanDigitCount + 2, 20);
        var tooManyDigits = DigitToken(tooManyDigitCount);

        // Act
        var normalized = Normalizer.NormalizePhone(tooManyDigits);

        // Assert
        Assert.Null(normalized);
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
    [MemberData(nameof(BlankValues))]
    public void NormalizeZip_InvalidOrMissing_ReturnsNull(string? input)
    {
        // Act
        var normalized = Normalizer.NormalizeZip(input);

        // Assert
        Assert.Null(normalized);
    }

    [Fact]
    public void NormalizeZip_TooFewDigitsForAZipCode_ReturnsNull()
    {
        // Arrange
        var tooFewDigitCount = Random.Shared.Next(1, ZipDigitCount);
        var tooFewDigits = DigitToken(tooFewDigitCount);

        // Act
        var normalized = Normalizer.NormalizeZip(tooFewDigits);

        // Assert
        Assert.Null(normalized);
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
        Assert.Equal($"{Uri.UriSchemeHttps}{Uri.SchemeDelimiter}{primaryHost}", normalized);
    }

    [Theory]
    [InlineData(null)]
    [MemberData(nameof(BlankValues))]
    public void NormalizeUrl_NullOrWhitespace_ReturnsNull(string? input)
    {
        // Act
        var normalized = Normalizer.NormalizeUrl(input);

        // Assert
        Assert.Null(normalized);
    }

    [Theory]
    [MemberData(nameof(RecognizedStateSpellings))]
    public void NormalizeState_RecognizedFormats_ReturnsTwoLetterCode(string input, string expected)
    {
        // Act
        var normalized = Normalizer.NormalizeState(input);

        // Assert
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [MemberData(nameof(BlankValues))]
    public void NormalizeState_MissingOrBlank_ReturnsNull(string? input)
    {
        // Act
        var normalized = Normalizer.NormalizeState(input);

        // Assert
        Assert.Null(normalized);
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
    [MemberData(nameof(BlankValues))]
    public void NormalizeBlank_NullOrWhitespace_ReturnsNull(string? input)
    {
        // Act
        var normalized = Normalizer.NormalizeBlank(input);

        // Assert
        Assert.Null(normalized);
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
        var propertyName = NewJsonPropertyName();
        using var doc = JsonDocument.Parse(JsonObject(new Dictionary<string, object>()));

        // Act
        var value = Normalizer.GetJsonString(doc.RootElement, propertyName);

        // Assert
        Assert.Null(value);
    }

    [Theory]
    [MemberData(nameof(BlankValues))]
    public void GetJsonString_BlankStringValue_ReturnsNull(string blankValue)
    {
        // Arrange
        var propertyName = NewJsonPropertyName();
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
        var propertyName = NewJsonPropertyName();
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
        var propertyName = NewJsonPropertyName();
        var cityName = TestValues.NewCity();
        using var doc = JsonDocument.Parse(JsonObject(new Dictionary<string, object> { [propertyName] = cityName }));

        // Act
        var value = Normalizer.GetJsonString(doc.RootElement, propertyName);

        // Assert
        Assert.Equal(cityName, value);
    }

    private static string JsonObject(Dictionary<string, object> properties) =>
        JsonSerializer.Serialize(properties);
}
