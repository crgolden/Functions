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
        var coverUrl = Generated.NewCoverImageUri();
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
        var coverUrl = Generated.NewCoverImageUri();
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
                Generated.NewCoverImageUri(),
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
    public void Deserialize_ReadsAnUnusableCoverAddressAsNoCover_RatherThanFailingTheWholeGrid()
    {
        // Arrange
        var unusable = Generated.NewToken();
        var grid = GridOf(ProductNode(
            unusable,
            Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit),
            Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit)));

        // Act
        var parsed = JsonSerializer.Deserialize<StoreCategoryGrid>(grid);

        // Assert
        var product = Assert.Single(parsed?.Products ?? []);
        Assert.Null(product.CoverImageUrl);
    }

    [Fact]
    public void Deserialize_KeepsEachProductsNodeAsTheVendorSentIt_UnusableAddressIncluded()
    {
        // Arrange
        var unusable = Generated.NewToken();
        var node = ProductNode(
            unusable,
            Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit),
            Generated.NewDisplayPrice(StoreCategoryProductFixtureConstants.CurrencyPrefix, StoreCategoryPrice.CentsPerUnit));

        // Act
        var parsed = JsonSerializer.Deserialize<StoreCategoryGrid>(GridOf(node));

        // Assert
        var product = Assert.Single(parsed?.Products ?? []);
        Assert.Equal(node, product.Raw);
        Assert.Contains(unusable, product.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WritesEachProductItself_SoAGridBuiltInCodeRoundTripsItsProducts()
    {
        // Arrange
        var grid = new StoreCategoryGrid
        {
            Products = [new StoreCategoryProduct { Id = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix), Name = Generated.NewGameTitle() }],
        };

        // Act
        var parsed = JsonSerializer.Deserialize<StoreCategoryGrid>(JsonSerializer.Serialize(grid));

        // Assert
        Assert.Equal(grid.Products.Select(product => product.Id), parsed?.Products.Select(product => product.Id));
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

    private static string GridOf(string productNode) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [StoreCategoryGrid.ProductsJsonName] = new[] { JsonElement.Parse(productNode) },
        });

    private static string ProductNode(Uri coverUrl, DisplayPrice basePrice, DisplayPrice discountedPrice) =>
        ProductNode(coverUrl.OriginalString, basePrice, discountedPrice);

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
