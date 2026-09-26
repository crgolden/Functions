namespace Functions.Tests.Unit;

using Functions.Curator.Library;

[Trait("Category", "Unit")]
public sealed class LibraryEntrySourcesTests
{
    [Fact]
    public void Sources_AreTheValuesCuratorsLibraryEntriesSourceColumnHolds()
    {
        // Act
        string[] sources = [LibraryEntrySources.Psn, LibraryEntrySources.Manual];

        // Assert
        Assert.Equal(["psn", "manual"], sources);
    }
}
