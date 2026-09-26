namespace Functions.Tests.Unit;

using System.Globalization;
using System.Text.Json;
using Functions.Curator.Psn;

[Trait("Category", "Unit")]
public sealed class PsnEntitlementPayloadTests
{
    [Fact]
    public void EntitlementAttributes_IsEmpty_WhenPsnOmitsTheKey()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new { id = Generated.NewEntitlementId() });

        // Act
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>(body);

        // Assert
        Assert.NotNull(payload);
        Assert.Empty(payload.EntitlementAttributes);
    }

    [Fact]
    public void EntitlementAttributes_IsEmpty_WhenPsnSendsNullForTheKey()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new { id = Generated.NewEntitlementId(), entitlementAttributes = (object?)null });

        // Act
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>(body);

        // Assert
        Assert.NotNull(payload);
        Assert.Empty(payload.EntitlementAttributes);
    }

    [Fact]
    public void Entitlements_IsEmpty_WhenPsnOmitsTheKey()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new { totalResults = 0 });

        // Act
        var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>(body);

        // Assert
        Assert.NotNull(page);
        Assert.Empty(page.Entitlements);
    }

    [Fact]
    public void TotalResults_IsNull_WhenPsnOmitsTheKey()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new { entitlements = Array.Empty<object>() });

        // Act
        var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>(body);

        // Assert
        Assert.NotNull(page);
        Assert.Null(page.TotalResults);
    }

    [Fact]
    public void TotalResults_IsZero_WhenPsnSendsZero()
    {
        // Arrange
        var body = JsonSerializer.Serialize(new { totalResults = 0 });

        // Act
        var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>(body);

        // Assert
        Assert.NotNull(page);
        Assert.Equal(0, page.TotalResults);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ActiveDate_KeepsTheNonZeroOffsetPsnSent(int offsetSign)
    {
        // Arrange
        var sentOffset = Generated.NewNonZeroUtcOffset() * offsetSign;
        var sent = Generated.NewReleaseTimestamp().ToOffset(sentOffset);
        var body = JsonSerializer.Serialize(new
        {
            id = Generated.NewEntitlementId(),
            activeDate = sent.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
        });

        // Act
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>(body);

        // Assert
        Assert.NotNull(payload);
        var parsed = Assert.IsType<DateTimeOffset>(payload.ActiveDate);
        Assert.Equal(sent.Hour, parsed.Hour);
        Assert.Equal(sentOffset, parsed.Offset);
    }

    [Fact]
    public void ActiveDate_ReadsTheUtcDesignatorAsAZeroOffset()
    {
        // Arrange
        var sent = Generated.NewReleaseTimestamp();
        var body = JsonSerializer.Serialize(new
        {
            id = Generated.NewEntitlementId(),
            activeDate = sent.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        });

        // Act
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>(body);

        // Assert
        Assert.NotNull(payload);
        var parsed = Assert.IsType<DateTimeOffset>(payload.ActiveDate);
        Assert.Equal(sent.Hour, parsed.Hour);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }
}
