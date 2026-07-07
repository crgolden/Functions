namespace Functions.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class SlugHelperTests
{
    [Theory]
    [InlineData("Grace Church", "grace-church")]
    [InlineData("", "")]
    [InlineData("  Grace", "grace")]
    [InlineData("Grace!!!Church", "grace-church")]
    [InlineData("Grace Church!", "grace-church")]
    public void ToSlug_Input_ProducesKebabCase(string input, string expected)
    {
        // Act
        var slug = SlugHelper.ToSlug(input);

        // Assert
        Assert.Equal(expected, slug);
    }
}
