namespace Functions.Tests.Unit;

using System.Globalization;
using System.Text.Json;
using Functions.Curator.Psn;
using Functions.Curator.Store;

[Trait("Category", "Unit")]
public sealed class StoreProductPayloadTests
{
    [Fact]
    public void ReleaseDate_ReadsTheUtcTimestampFormTheStorefrontSends()
    {
        // Arrange
        var released = Generated.NewReleaseTimestamp();
        var body = JsonSerializer.Serialize(new { releaseDate = released.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) });

        // Act
        var product = JsonSerializer.Deserialize<StoreProductNode>(body);

        // Assert
        Assert.Equal(released, product?.ReleaseDate);
    }

    [Fact]
    public void ReleaseDate_ReadsADateWithoutATime_AsMidnightUtc()
    {
        // Arrange
        var releaseDate = Generated.NewReleaseDate();
        var body = JsonSerializer.Serialize(new { releaseDate = releaseDate.ToString(ReleaseYearFixtureConstants.RawgBareDateFormat, CultureInfo.InvariantCulture) });

        // Act
        var product = JsonSerializer.Deserialize<StoreProductNode>(body);

        // Assert
        Assert.Equal(new DateTimeOffset(releaseDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), product?.ReleaseDate);
    }

    [Fact]
    public void ReleaseDate_IsAbsent_WhenTheStorefrontSendsTextThatIsNotADate_AndTheRestOfTheProductStillReads()
    {
        // Arrange
        var productId = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix);
        var body = JsonSerializer.Serialize(new { releaseDate = Generated.NewGameTitle(), id = productId });

        // Act
        var product = JsonSerializer.Deserialize<StoreProductNode>(body);

        // Assert
        const string reason =
            "An unparseable release date must cost that one product its date. Failing the body turns it into "
            + "store_unreachable, which stops the whole nightly pass over a single odd product.";

        Assert.True(product?.ReleaseDate is null, reason);
        Assert.Equal(productId, product?.Id);
    }

    [Fact]
    public void ReleaseDate_IsAbsent_WhenTheStorefrontSendsAnObject_AndTheRestOfTheProductStillReads()
    {
        // Arrange
        var productId = Generated.NewStoreProductId(TitlePlatform.Ps4TitleIdPrefix);
        var body = JsonSerializer.Serialize(new { releaseDate = new { type = Generated.NewReleaseDateType() }, id = productId });

        // Act
        var product = JsonSerializer.Deserialize<StoreProductNode>(body);

        // Assert
        Assert.Null(product?.ReleaseDate);
        Assert.Equal(productId, product?.Id);
    }

    [Fact]
    public void ReleaseDate_RoundTripsThroughTheConverter()
    {
        // Arrange
        var node = new StoreProductNode { ReleaseDate = Generated.NewTimestampWithNonZeroOffset() };

        // Act
        var roundTripped = JsonSerializer.Deserialize<StoreProductNode>(JsonSerializer.Serialize(node));

        // Assert
        Assert.Equal(node.ReleaseDate, roundTripped?.ReleaseDate);
    }

    [Fact]
    public void ReleaseDate_WritesAnAbsentDateAsNull()
    {
        // Arrange
        var node = new StoreProductNode();

        // Act
        var roundTripped = JsonSerializer.Deserialize<StoreProductNode>(JsonSerializer.Serialize(node));

        // Assert
        Assert.Null(roundTripped?.ReleaseDate);
    }
}
