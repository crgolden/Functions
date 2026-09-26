namespace Functions.Tests.Unit;

using System.Globalization;
using System.Text.Json;
using Functions.Curator.Psn;
using Functions.Curator.Store;

[Trait("Category", "Unit")]
public sealed class StoreCategoryProductTests
{
    [Fact]
    public void Deserialize_ReadsTheFieldsTheCrawlAdmitsOn()
    {
        // Arrange
        var coverUrl = Generated.NewCoverImageAddress();
        var basePrice = Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit);
        var discountedPrice = Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit);
        var node = ProductNode(coverUrl, basePrice, discountedPrice);

        // Act
        var product = JsonSerializer.Deserialize<StoreCategoryProduct>(node);

        // Assert
        Assert.NotNull(product);
        Assert.True(product.IsFullGame);
        Assert.Equal(coverUrl, product.CoverImageUrl);
        Assert.Equal(basePrice.Cents, product.Price?.BaseCents);
        Assert.Equal(discountedPrice.Cents, product.Price?.DiscountedCents);
        Assert.False(product.Price?.IsFree);
    }

    [Fact]
    public void CoverImageUrl_PrefersTheCoverArtRole_OverEveryOtherMediaEntry()
    {
        // Arrange
        var coverUrl = Generated.NewCoverImageAddress();
        var node = ProductNode(
            coverUrl,
            Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit),
            Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit));

        // Act
        var product = JsonSerializer.Deserialize<StoreCategoryProduct>(node);

        // Assert
        Assert.Equal(coverUrl, product?.CoverImageUrl);
    }

    [Fact]
    public void Roundtrip_KeepsTheFieldsTheModelDoesNotMap_SoTheCachedNodeIsTheVendorsOwn()
    {
        // Arrange
        var product = JsonSerializer.Deserialize<StoreCategoryProduct>(
            ProductNode(
                Generated.NewCoverImageAddress(),
                Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit),
                Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit)));

        // Act
        var written = JsonSerializer.Serialize(product);

        // Assert
        using var reread = JsonDocument.Parse(written);
        Assert.True(reread.RootElement.TryGetProperty(StoreCategoryProductFixtureConstants.SkusKey, out _));
        Assert.True(reread.RootElement.TryGetProperty(StoreCategoryProductFixtureConstants.TypeNameKey, out _));
        var price = reread.RootElement.GetProperty(StoreCategoryProductFixtureConstants.PriceKey);
        Assert.True(price.TryGetProperty(StoreCategoryProductFixtureConstants.UpsellTextKey, out _));
    }

    [Fact]
    public void Cents_IsNull_WhenTheGatewaySentNoPriceAtAll()
    {
        // Act
        var cents = StoreCategoryPrice.Cents(null);

        // Assert
        Assert.Null(cents);
    }

    [Fact]
    public void Cents_IsNull_WhenTheDisplayPriceIsAWordRatherThanAnAmount()
    {
        // Act
        var free = StoreCategoryPrice.Cents(StoreCategoryProductFixtureConstants.FreeDisplayPrice);
        var included = StoreCategoryPrice.Cents(StoreCategoryProductFixtureConstants.IncludedDisplayPrice);

        // Assert
        Assert.Null(free);
        Assert.Null(included);
    }

    [Fact]
    public void Cents_ReadsThroughAThousandsSeparator()
    {
        // Arrange
        var thousands = Random.Shared.Next(1, 10);
        var units = Random.Shared.Next(100, 1000);
        var displayPrice = StoreCategoryProductFixtureConstants.CurrencyPrefix
            + thousands.ToString(CultureInfo.InvariantCulture)
            + StoreCategoryProductFixtureConstants.ThousandsSeparator
            + units.ToString(CultureInfo.InvariantCulture)
            + StoreCategoryProductFixtureConstants.CentsSuffix;

        // Act
        var cents = StoreCategoryPrice.Cents(displayPrice);

        // Assert
        Assert.Equal(
            (((thousands * StoreCategoryProductFixtureConstants.ThousandsPlaceValue) + units) * StoreCategoryPrice.CentsPerUnit)
                + StoreCategoryProductFixtureConstants.CentsPart,
            cents);
    }

    private static string ProductNode(string coverUrl, DisplayPrice basePrice, DisplayPrice discountedPrice) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [StoreCategoryProductFixtureConstants.TypeNameKey] =
                StoreCategoryProductFixtureConstants.ProductTypeName,
            [StoreCategoryProduct.IdJsonName] = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix),
            [StoreCategoryProduct.NameJsonName] = Generated.NewGameTitle(),
            [StoreCategoryProduct.NpTitleIdJsonName] = Generated.NewTitleId(TitlePlatform.Ps4TitleIdPrefix),
            [StoreCategoryProduct.ClassificationJsonName] = StoreCategoryProduct.FullGameClassification,
            [StoreCategoryProduct.PlatformsJsonName] = new[] { Generated.NewGameTitle() },
            [StoreCategoryProduct.MediaJsonName] = new object[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [StoreCategoryProductFixtureConstants.TypeNameKey] =
                        StoreCategoryProductFixtureConstants.MediaTypeName,
                    [StoreMediaEntry.RoleJsonName] = StoreCategoryProductFixtureConstants.PreviewRole,
                    [StoreMediaEntry.TypeJsonName] = StoreCategoryProductFixtureConstants.VideoMediaType,
                    [StoreMediaEntry.UrlJsonName] = Generated.NewCoverImageAddress(),
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [StoreCategoryProductFixtureConstants.TypeNameKey] =
                        StoreCategoryProductFixtureConstants.MediaTypeName,
                    [StoreMediaEntry.RoleJsonName] = StoreCoverArt.RolePreference[0],
                    [StoreMediaEntry.TypeJsonName] = StoreCoverArt.ImageMediaType,
                    [StoreMediaEntry.UrlJsonName] = coverUrl,
                },
            },
            [StoreCategoryProductFixtureConstants.PriceKey] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [StoreCategoryProductFixtureConstants.TypeNameKey] =
                    StoreCategoryProductFixtureConstants.PriceTypeName,
                [StoreCategoryPrice.BasePriceJsonName] = basePrice.Text,
                [StoreCategoryPrice.DiscountedPriceJsonName] = discountedPrice.Text,
                [StoreCategoryPrice.DiscountTextJsonName] = Generated.NewGameTitle(),
                [StoreCategoryPrice.IsFreeJsonName] = false,
                [StoreCategoryPrice.IsTiedToSubscriptionJsonName] = false,
                [StoreCategoryProductFixtureConstants.UpsellTextKey] = Generated.NewGameTitle(),
            },
            [StoreCategoryProductFixtureConstants.SkusKey] = new object[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [StoreCategoryProductFixtureConstants.TypeNameKey] =
                        StoreCategoryProductFixtureConstants.SkuTypeName,
                    [StoreCategoryProduct.IdJsonName] = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix),
                },
            },
        });
}
