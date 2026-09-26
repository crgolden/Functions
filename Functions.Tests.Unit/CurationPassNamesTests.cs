namespace Functions.Tests.Unit;

using Functions.Curator.Enrichment;

[Trait("Category", "Unit")]
public sealed class CurationPassNamesTests
{
    [Fact]
    public void PassNames_AreTheRowKeysCuratorsPythonPassesReadAndWrite()
    {
        // Act
        string[] passNames = [CurationPassNames.FranchiseReclassification, CurationPassNames.TierReclassification];

        // Assert
        Assert.Equal(["franchise_reclassification", "tier_reclassification"], passNames);
    }
}
