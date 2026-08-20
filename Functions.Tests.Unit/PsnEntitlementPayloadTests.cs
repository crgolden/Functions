namespace Functions.Tests.Unit;

using System.Text.Json;
using Curator.Psn;

[Trait("Category", "Unit")]
public sealed class PsnEntitlementPayloadTests
{
    [Fact]
    public void EntitlementAttributes_IsEmpty_WhenPsnOmitsTheKey()
    {
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>("""{"id": "ent-1"}""");

        Assert.NotNull(payload);
        Assert.Empty(payload.EntitlementAttributes);
    }

    [Fact]
    public void EntitlementAttributes_IsEmpty_WhenPsnSendsNullForTheKey()
    {
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>(
            """{"id": "ent-1", "entitlementAttributes": null}""");

        Assert.NotNull(payload);
        Assert.Empty(payload.EntitlementAttributes);
    }

    [Fact]
    public void Entitlements_IsEmpty_WhenPsnOmitsTheKey()
    {
        var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>("""{"totalResults": 0}""");

        Assert.NotNull(page);
        Assert.Empty(page.Entitlements);
    }

    [Fact]
    public void TotalResults_IsZero_WhenPsnOmitsTheKey()
    {
        var page = JsonSerializer.Deserialize<PsnEntitlementsResponse>("""{"entitlements": []}""");

        Assert.NotNull(page);
        Assert.Equal(0, page.TotalResults);
    }

    [Theory]
    [InlineData("2019-04-05T18:22:11Z", 18, 0)]
    [InlineData("2019-04-05T18:22:11+09:00", 18, 9)]
    [InlineData("2019-04-05T18:22:11-04:00", 18, -4)]
    public void ActiveDate_KeepsTheOffsetPsnSent(string activeDate, int expectedHour, int expectedOffsetHours)
    {
        var payload = JsonSerializer.Deserialize<PsnEntitlementPayload>(
            $$"""{"id": "ent-1", "activeDate": "{{activeDate}}"}""");

        Assert.NotNull(payload);
        var parsed = Assert.IsType<DateTimeOffset>(payload.ActiveDate);
        Assert.Equal(expectedHour, parsed.Hour);
        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), parsed.Offset);
    }
}
