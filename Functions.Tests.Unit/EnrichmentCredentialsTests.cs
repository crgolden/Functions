namespace Functions.Tests.Unit;

using Curator.Enrichment;
using Curator.OpenCritic;
using Curator.Psn;
using Curator.Rawg;

[Trait("Category", "Unit")]
public sealed class EnrichmentCredentialsTests
{
    [Fact]
    public void Without_WhenGivenRawg_DropsOnlyTheRawgCredential()
    {
        // Arrange
        var credentials = All();

        // Act
        var remaining = credentials.Without(EnrichmentProvider.Rawg);

        // Assert
        Assert.Null(remaining.Rawg);
        Assert.NotNull(remaining.OpenCritic);
        Assert.NotNull(remaining.Psn);
    }

    [Fact]
    public void Without_WhenGivenOpenCritic_DropsOnlyTheOpenCriticCredential()
    {
        // Arrange
        var credentials = All();

        // Act
        var remaining = credentials.Without(EnrichmentProvider.OpenCritic);

        // Assert
        Assert.Null(remaining.OpenCritic);
        Assert.NotNull(remaining.Rawg);
        Assert.NotNull(remaining.Psn);
    }

    [Fact]
    public void Without_WhenGivenPsn_DropsOnlyTheSessionRotation()
    {
        // Arrange
        var credentials = All();

        // Act
        var remaining = credentials.Without(EnrichmentProvider.Psn);

        // Assert
        Assert.Null(remaining.Psn);
        Assert.NotNull(remaining.Rawg);
        Assert.NotNull(remaining.OpenCritic);
    }

    [Fact]
    public void Without_LeavesTheOriginalUntouched()
    {
        // Arrange
        var credentials = All();

        // Act
        credentials.Without(EnrichmentProvider.Rawg);

        // Assert
        Assert.NotNull(credentials.Rawg);
    }

    private static EnrichmentCredentials All() => new()
    {
        Rawg = new RawgCredential { ApiKey = Guid.NewGuid().ToString() },
        OpenCritic = new OpenCriticCredential { RapidApiKey = Guid.NewGuid().ToString() },
        Psn = new PsnSessionRotation([new PsnSession(null, null, NullPsnRateLimiter.Unthrottled)]),
    };
}
