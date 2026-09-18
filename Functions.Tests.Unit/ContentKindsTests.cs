namespace Functions.Tests.Unit;

using Curator.Catalog;

[Trait("Category", "Unit")]
public sealed class ContentKindsTests
{
    public static TheoryData<ContentKind, string> KindsAndTheirWireNames() => new()
    {
        { ContentKind.Game, ContentKinds.Game },
        { ContentKind.MediaApp, ContentKinds.MediaApp },
    };

    public static TheoryData<ContentKind> EveryKind() => [.. Enum.GetValues<ContentKind>()];

    [Theory]
    [MemberData(nameof(KindsAndTheirWireNames))]
    public void ToWireName_WritesTheValueCuratorsContentKindColumnStores(ContentKind kind, string expectedWireName)
    {
        // Act
        var wireName = kind.ToWireName();

        // Assert
        Assert.Equal(expectedWireName, wireName);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void ToWireName_HasAWireNameForEveryKind_SoANewMemberCannotReachTheDatabaseUnmapped(ContentKind kind)
    {
        // Act
        var exception = Record.Exception(() => kind.ToWireName());

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void FromPackageType_ReadsAMediaPackageAsAMediaApp()
    {
        // Act
        var kind = ContentKinds.FromPackageType(ContentKinds.MediaAppPackageType);

        // Assert
        Assert.Equal(ContentKind.MediaApp, kind);
    }
}
