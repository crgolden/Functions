namespace Functions.Tests.Unit;

using Curator.Catalog;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class MergeServiceTests
{
    [Fact]
    public void MergeByProductIdAndName_MergesTwoConceptGroups_WhenProductIdAndNameBothAgree()
    {
        // Arrange
        var name = NewGameName();
        var productId = NewProductId();
        var firstEntitlementId = NewEntitlementId();
        var secondEntitlementId = NewEntitlementId();
        var groups = Groups(
            (NewGroupKey(), Entry(name, productId, NewConceptId(), firstEntitlementId)),
            (NewGroupKey(), Entry(name, productId, NewConceptId(), secondEntitlementId)));

        // Act
        var merged = MergeService.MergeByProductIdAndName(groups);

        // Assert
        var group = Assert.Single(merged);
        Assert.Equal([firstEntitlementId, secondEntitlementId], group.Select(entry => entry.EntitlementId));
    }

    [Fact]
    public void MergeByProductIdAndName_LeavesGroupsSeparate_WhenTheySharedAProductIdButNotAName()
    {
        // Arrange
        var productId = NewProductId();
        var groups = Groups(
            (NewGroupKey(), Entry(NewGameName(), productId, NewConceptId(), NewEntitlementId())),
            (NewGroupKey(), Entry(NewGameName(), productId, NewConceptId(), NewEntitlementId())));

        // Act
        var merged = MergeService.MergeByProductIdAndName(groups);

        // Assert
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void MergeByProductIdAndName_MatchesNamesCaseInsensitively()
    {
        // Arrange
        var name = NewGameName();
        var productId = NewProductId();
        var groups = Groups(
            (NewGroupKey(), Entry(name, productId, NewConceptId(), NewEntitlementId())),
            (NewGroupKey(), Entry(name.ToUpperInvariant(), productId, NewConceptId(), NewEntitlementId())));

        // Act
        var merged = MergeService.MergeByProductIdAndName(groups);

        // Assert
        Assert.Single(merged);
    }

    [Fact]
    public void MergeByProductIdAndName_PassesThroughAGroupWithNoProductId()
    {
        // Arrange
        var groups = Groups(
            (NewGroupKey(), Entry(NewGameName(), productId: null, NewConceptId(), NewEntitlementId())),
            (NewGroupKey(), Entry(NewGameName(), productId: null, NewConceptId(), NewEntitlementId())));

        // Act
        var merged = MergeService.MergeByProductIdAndName(groups);

        // Assert
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void MergeByProductIdAndName_PassesThroughASoleGroupHoldingAProductId()
    {
        // Arrange
        var entitlementId = NewEntitlementId();
        var groups = Groups((NewGroupKey(), Entry(NewGameName(), NewProductId(), NewConceptId(), entitlementId)));

        // Act
        var merged = MergeService.MergeByProductIdAndName(groups);

        // Assert
        var group = Assert.Single(merged);
        Assert.Equal(entitlementId, Assert.Single(group).EntitlementId);
    }

    [Fact]
    public void MergeByProductIdAndName_PreservesEntryOrderWithinAMergedGroup()
    {
        // Arrange
        var name = NewGameName();
        var productId = NewProductId();
        var firstEntitlementId = NewEntitlementId();
        var secondEntitlementId = NewEntitlementId();
        var thirdEntitlementId = NewEntitlementId();
        var groups = Groups(
            (NewGroupKey(), Entry(name, productId, NewConceptId(), firstEntitlementId)),
            (NewGroupKey(), Entry(name, productId, NewConceptId(), secondEntitlementId)),
            (NewGroupKey(), Entry(name, productId, NewConceptId(), thirdEntitlementId)));

        // Act
        var merged = MergeService.MergeByProductIdAndName(groups);

        // Assert
        var group = Assert.Single(merged);
        Assert.Equal(
            [firstEntitlementId, secondEntitlementId, thirdEntitlementId],
            group.Select(entry => entry.EntitlementId));
    }

    [Fact]
    public void MergeByProductIdAndName_ReturnsMergedGroupsBeforeUntouchedOnes()
    {
        // Arrange
        var pairName = NewGameName();
        var pairProductId = NewProductId();
        var soloEntitlementId = NewEntitlementId();
        var firstEntitlementId = NewEntitlementId();
        var secondEntitlementId = NewEntitlementId();
        var groups = Groups(
            (NewGroupKey(), Entry(NewGameName(), NewProductId(), NewConceptId(), soloEntitlementId)),
            (NewGroupKey(), Entry(pairName, pairProductId, NewConceptId(), firstEntitlementId)),
            (NewGroupKey(), Entry(pairName, pairProductId, NewConceptId(), secondEntitlementId)));

        // Act
        var merged = MergeService.MergeByProductIdAndName(groups);

        // Assert
        Assert.Equal([firstEntitlementId, secondEntitlementId], merged[0].Select(entry => entry.EntitlementId));
        Assert.Equal(soloEntitlementId, Assert.Single(merged[1]).EntitlementId);
    }

    private static GroupedEntry Entry(string name, string? productId, string conceptId, string entitlementId) =>
        new(name, PackageType: NewPackageType(), conceptId, productId, entitlementId);

    private static List<KeyValuePair<string, IReadOnlyList<GroupedEntry>>> Groups(
        params (string Key, GroupedEntry Entry)[] entries) =>
        entries
            .Select(pair => new KeyValuePair<string, IReadOnlyList<GroupedEntry>>(pair.Key, [pair.Entry]))
            .ToList();

    private static string NewGameName() => TestValues.NewGameName();

    private static string NewProductId() => TestValues.NewProductId();

    private static string NewConceptId() => TestValues.NewConceptId();

    private static string NewEntitlementId() => TestValues.NewEntitlementId();

    private static string NewGroupKey() => $"group-{Guid.NewGuid():N}";

    private static string NewPackageType() => $"pkg-{Guid.NewGuid():N}";
}
