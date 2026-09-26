namespace Functions.Tests.Unit;

using System.Globalization;
using System.Text.Json;
using Functions.Churches;
using static Functions.Tests.Unit.NormalizerFixtureConstants;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class NormalizerTests
{
    public static TheoryData<string> BlankValues() => [string.Empty, NewBlankRun()];

    public static TheoryData<string, string> FullStateNames() =>
        new(Normalizer.StateCodesByFullName.Select(pair => (pair.Key, pair.Value)));

    public static TheoryData<string, string> RecognizedStateSpellings() => new()
    {
        { ColoradoCode, ColoradoCode },
        { ColoradoCode.ToLowerInvariant(), ColoradoCode },
        { OhioName, OhioCode },
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
        var primaryHost = Generated.NewHost();
        var secondaryHost = Generated.NewHost();
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
    [MemberData(nameof(FullStateNames))]
    public void NormalizeState_FullStateName_ReturnsItsUspsCode(string fullName, string expected)
    {
        // Act
        var normalized = Normalizer.NormalizeState(fullName);

        // Assert
        Assert.Equal(expected, normalized);
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

    [Fact]
    public void NormalizeState_TwoLettersOutsideTheUspsSet_ReturnsNull()
    {
        // Arrange
        var outsideTheSet = Generated.NewUnrecognizedStateCode();

        // Act
        var normalized = Normalizer.NormalizeState(outsideTheSet);

        // Assert
        Assert.Null(normalized);
    }

    [Fact]
    public void NormalizeState_SalvagedLettersOutsideTheUspsSet_ReturnsNull()
    {
        // Arrange
        var punctuatedOutsideTheSet = $"-{Generated.NewUnrecognizedStateCode()}";

        // Act
        var normalized = Normalizer.NormalizeState(punctuatedOutsideTheSet);

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
        var cityName = Generated.NewCity();
        var formattedValue = string.Format(CultureInfo.InvariantCulture, valueFormat, cityName);

        // Act
        var normalized = Normalizer.NormalizeBlank(formattedValue);

        // Assert
        Assert.Equal(cityName, normalized);
    }

    [Theory]
    [InlineData(ZeroWidthSpace)]
    [InlineData(SoftHyphen)]
    [InlineData(LeftToRightMark)]
    [InlineData(ByteOrderMark)]
    public void NormalizeBlank_InvisibleFormattingCharacter_IsStripped(string invisible)
    {
        // Arrange
        var churchName = Generated.NewChurchName();
        var carryingInvisibles = invisible + churchName + invisible;

        // Act
        var normalized = Normalizer.NormalizeBlank(carryingInvisibles);

        // Assert
        Assert.Equal(churchName, normalized);
    }

    [Fact]
    public void NormalizeBlank_InvisibleCharacterInsideAWord_IsStripped()
    {
        // Arrange
        var beforeBreak = Generated.LowercaseToken(5);
        var afterBreak = Generated.LowercaseToken(4);

        // Act
        var normalized = Normalizer.NormalizeBlank($"{beforeBreak}{ZeroWidthSpace}{afterBreak}");

        // Assert
        Assert.Equal(beforeBreak + afterBreak, normalized);
    }

    [Fact]
    public void NormalizeBlank_NoBreakSpace_BecomesAnOrdinarySpace()
    {
        // Arrange
        var firstWord = Generated.LowercaseToken(6);
        var secondWord = Generated.LowercaseToken(7);

        // Act
        var normalized = Normalizer.NormalizeBlank($"{firstWord}{NoBreakSpace}{secondWord}");

        // Assert
        Assert.Equal($"{firstWord} {secondWord}", normalized);
    }

    [Fact]
    public void NormalizeBlank_OnlyInvisibleCharacters_ReturnsNull()
    {
        // Act
        var normalized = Normalizer.NormalizeBlank(ZeroWidthSpace + SoftHyphen + LeftToRightMark);

        // Assert
        Assert.Null(normalized);
    }

    [Fact]
    public void NormalizeBlank_InteriorNewline_IsPreserved()
    {
        // Arrange
        var firstParagraph = Generated.LowercaseToken(8);
        var secondParagraph = Generated.LowercaseToken(9);

        // Act
        var normalized = Normalizer.NormalizeBlank(firstParagraph + ParagraphBreak + secondParagraph);

        // Assert
        Assert.Equal(firstParagraph + ParagraphBreak + secondParagraph, normalized);
    }

    [Fact]
    public void NormalizeBlank_InteriorTab_IsPreserved()
    {
        // Arrange
        var beforeTab = Generated.LowercaseToken(6);
        var afterTab = Generated.LowercaseToken(7);

        // Act
        var normalized = Normalizer.NormalizeBlank(beforeTab + Tab + afterTab);

        // Assert
        Assert.Equal(beforeTab + Tab + afterTab, normalized);
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
        var cityName = Generated.NewCity();
        using var doc = JsonDocument.Parse(JsonObject(new Dictionary<string, object> { [propertyName] = cityName }));

        // Act
        var value = Normalizer.GetJsonString(doc.RootElement, propertyName);

        // Assert
        Assert.Equal(cityName, value);
    }

    private static string JsonObject(Dictionary<string, object> properties) =>
        JsonSerializer.Serialize(properties);
}
