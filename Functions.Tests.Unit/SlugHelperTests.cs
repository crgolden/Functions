namespace Functions.Tests.Unit;

using Churches;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class SlugHelperTests
{
    [Fact]
    public void ToSlug_TwoWords_JoinsThemWithOneDashAndLowersThem()
    {
        // Arrange
        var firstWord = TestValues.LowercaseToken(6);
        var secondWord = TestValues.LowercaseToken(8);

        // Act
        var slug = SlugHelper.ToSlug($"{firstWord.ToUpperInvariant()} {secondWord.ToUpperInvariant()}");

        // Assert
        Assert.Equal($"{firstWord}-{secondWord}", slug);
    }

    [Fact]
    public void ToSlug_ABlankValue_ProducesNothing()
    {
        // Arrange
        var blank = TestValues.NewBlankRun();

        // Act
        var slug = SlugHelper.ToSlug(blank);

        // Assert
        Assert.Empty(slug);
    }

    [Fact]
    public void ToSlug_LeadingSeparators_AreDropped()
    {
        // Arrange
        var word = TestValues.LowercaseToken(7);

        // Act
        var slug = SlugHelper.ToSlug($"{TestValues.NewBlankRun()}{word}");

        // Assert
        Assert.Equal(word, slug);
    }

    [Fact]
    public void ToSlug_ARunOfPunctuationBetweenWords_CollapsesToOneDash()
    {
        // Arrange
        var firstWord = TestValues.LowercaseToken(6);
        var secondWord = TestValues.LowercaseToken(8);
        var punctuationRun = TestValues.NewPunctuationRun();

        // Act
        var slug = SlugHelper.ToSlug($"{firstWord}{punctuationRun}{secondWord}");

        // Assert
        Assert.Equal($"{firstWord}-{secondWord}", slug);
    }

    [Fact]
    public void ToSlug_TrailingSeparators_AreDropped()
    {
        // Arrange
        var firstWord = TestValues.LowercaseToken(6);
        var secondWord = TestValues.LowercaseToken(8);

        // Act
        var slug = SlugHelper.ToSlug($"{firstWord} {secondWord}!");

        // Assert
        Assert.Equal($"{firstWord}-{secondWord}", slug);
    }
}