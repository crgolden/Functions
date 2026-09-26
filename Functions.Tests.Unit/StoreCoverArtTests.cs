namespace Functions.Tests.Unit;

using Functions.Curator.Store;

[Trait("Category", "Unit")]
public sealed class StoreCoverArtTests
{
    [Fact]
    public void Best_TakesTheEarlierRole_WhenTheStorefrontPublishesTwoImageRoles()
    {
        // Arrange
        var preferred = Generated.NewCoverImageAddress();
        var alsoAnImage = Generated.NewCoverImageAddress();
        StoreMediaEntry[] media =
        [
            Image(StoreCoverArt.RolePreference[1], alsoAnImage),
            Image(StoreCoverArt.RolePreference[0], preferred),
        ];

        // Act
        var best = StoreCoverArt.Best(media);

        // Assert
        Assert.Equal(preferred, best);
    }

    [Fact]
    public void Best_PassesOverAnEntryThatIsNotAnImage_EvenUnderThePreferredRole()
    {
        // Arrange
        var image = Generated.NewCoverImageAddress();
        StoreMediaEntry[] media =
        [
            new() { Role = StoreCoverArt.RolePreference[0], Type = Generated.NewToken(), Url = Generated.NewCoverImageAddress() },
            Image(StoreCoverArt.RolePreference[1], image),
        ];

        // Act
        var best = StoreCoverArt.Best(media);

        // Assert
        Assert.Equal(image, best);
    }

    [Fact]
    public void Best_PassesOverAnEntryCarryingNoAddress_SoAPreferredRoleWithNothingBehindItIsNotTheAnswer()
    {
        // Arrange
        var image = Generated.NewCoverImageAddress();
        StoreMediaEntry[] media =
        [
            Image(StoreCoverArt.RolePreference[0], null),
            Image(StoreCoverArt.RolePreference[1], image),
        ];

        // Act
        var best = StoreCoverArt.Best(media);

        // Assert
        Assert.Equal(image, best);
    }

    [Fact]
    public void Best_IsNull_WhenNoEntryCarriesARoleTheCrawlPrefers()
    {
        // Arrange
        StoreMediaEntry[] media = [Image(Generated.NewToken(), Generated.NewCoverImageAddress())];

        // Act
        var best = StoreCoverArt.Best(media);

        // Assert
        Assert.Null(best);
    }

    [Fact]
    public void Best_IsNull_WhenTheProductCarriesNoMediaAtAll()
    {
        // Act
        var best = StoreCoverArt.Best([]);

        // Assert
        Assert.Null(best);
    }

    private static StoreMediaEntry Image(string role, string? url) =>
        new() { Role = role, Type = StoreCoverArt.ImageMediaType, Url = url };
}
