namespace Functions.Tests.Unit;

using System.Text.Json;
using Curator.Psn;

[Trait("Category", "Unit")]
public sealed class PsnEntitlementPayloadTests
{
    [Fact]
    public void EntitlementAttributes_IsEmpty_WhenPsnOmitsTheKey()
    {
        // Act
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>("""{"id": "ent-1"}""");

        // Assert
        Assert.NotNull(payload);
        Assert.Empty(payload.EntitlementAttributes);
    }

    [Fact]
    public void EntitlementAttributes_IsEmpty_WhenPsnSendsNullForTheKey()
    {
        // Act
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>(
            """{"id": "ent-1", "entitlementAttributes": null}""");

        // Assert
        Assert.NotNull(payload);
        Assert.Empty(payload.EntitlementAttributes);
    }

    [Fact]
    public void Entitlements_IsEmpty_WhenPsnOmitsTheKey()
    {
        // Act
        var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>("""{"totalResults": 0}""");

        // Assert
        Assert.NotNull(page);
        Assert.Empty(page.Entitlements);
    }

    [Fact]
    public void TotalResults_IsNull_WhenPsnOmitsTheKey()
    {
        // Act
        var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>("""{"entitlements": []}""");

        // Assert
        Assert.NotNull(page);
        Assert.Null(page.TotalResults);
    }

    [Fact]
    public void TotalResults_IsZero_WhenPsnSendsZero()
    {
        // Act
        var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>("""{"totalResults": 0}""");

        // Assert
        Assert.NotNull(page);
        Assert.Equal(0, page.TotalResults);
    }

    [Theory]
    [InlineData("2019-04-05T18:22:11Z", 18, 0)]
    [InlineData("2019-04-05T18:22:11+09:00", 18, 9)]
    [InlineData("2019-04-05T18:22:11-04:00", 18, -4)]
    public void ActiveDate_KeepsTheOffsetPsnSent(string activeDate, int expectedHour, int expectedOffsetHours)
    {
        // Act
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>(
            $$"""{"id": "ent-1", "activeDate": "{{activeDate}}"}""");

        // Assert
        Assert.NotNull(payload);
        var parsed = Assert.IsType<DateTimeOffset>(payload.ActiveDate);
        Assert.Equal(expectedHour, parsed.Hour);
        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), parsed.Offset);
    }
}
