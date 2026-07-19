namespace Functions.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class NormalizerTests
{
    // --- NormalizePhone ---
    [Theory]
    [InlineData("(303) 555-1234", "+13035551234")]
    [InlineData("303-555-1234", "+13035551234")]
    [InlineData("3035551234", "+13035551234")]
    [InlineData("+13035551234", "+13035551234")]
    [InlineData("1-303-555-1234", "+13035551234")]
    [InlineData("13035551234", "+13035551234")]
    public void NormalizePhone_ValidFormats_ReturnsE164(string input, string expected)
    {
        Assert.Equal(expected, Normalizer.NormalizePhone(input));
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

    // --- NormalizeZip ---
    [Theory]
    [InlineData("85001", "85001")]
    [InlineData("85001-1234", "85001")]
    [InlineData("85 001", "85001")]
    [InlineData("85001-", "85001")]
    public void NormalizeZip_ValidFormats_ReturnsFiveDigits(string input, string expected)
    {
        Assert.Equal(expected, Normalizer.NormalizeZip(input));
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

    // --- NormalizeUrl ---
    [Theory]
    [InlineData("https://grace.example", "https://grace.example")]
    [InlineData("https://grace.example/", "https://grace.example")]
    [InlineData("http://grace.example", "https://grace.example")]
    [InlineData("http://grace.example/", "https://grace.example")]
    [InlineData("grace.example", "https://grace.example")]
    [InlineData("  grace.example/  ", "https://grace.example")]
    [InlineData("https://grace.example;http://grace-school.example", "https://grace.example")]
    [InlineData("grace.example;grace-school.example", "https://grace.example")]
    public void NormalizeUrl_VariousSchemes_ReturnsHttpsWithoutTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, Normalizer.NormalizeUrl(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeUrl_NullOrWhitespace_ReturnsNull(string? input)
    {
        Assert.Null(Normalizer.NormalizeUrl(input));
    }

    // --- NormalizeState ---
    [Theory]
    [InlineData("CO", "CO")] // already a 2-letter code
    [InlineData("co", "CO")] // lowercased code -> uppercased
    [InlineData("Ohio", "OH")] // full state name
    [InlineData("alaska", "AK")] // full name, lowercased — the shape OpenAI enrichment produces
    [InlineData("W. Va.", "WV")] // hand-typed abbreviation
    [InlineData("-IL", "IL")] // punctuation stripped
    public void NormalizeState_RecognizedFormats_ReturnsTwoLetterCode(string input, string expected)
    {
        Assert.Equal(expected, Normalizer.NormalizeState(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Atlantis")]
    public void NormalizeState_MissingOrUnrecognized_ReturnsNull(string? input)
    {
        Assert.Null(Normalizer.NormalizeState(input));
    }
}
