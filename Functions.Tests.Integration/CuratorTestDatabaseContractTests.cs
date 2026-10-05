namespace Functions.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class CuratorTestDatabaseContractTests
{
    [Fact]
    public void IsDisposableDatabase_TriageSuffix_ReturnsTrue()
    {
        var triageDatabase = Generated.NewDatabaseName() + CuratorTestDatabaseContractConstants.TriageDatabaseNameSuffix;

        Assert.True(CuratorTestDatabaseContract.IsDisposableDatabase(triageDatabase));
    }

    [Fact]
    public void IsDisposableDatabase_TestSuffix_ReturnsTrue()
    {
        var testDatabase = Generated.NewDatabaseName() + CuratorTestDatabaseContractConstants.TestDatabaseNameSuffix;

        Assert.True(CuratorTestDatabaseContract.IsDisposableDatabase(testDatabase));
    }

    [Fact]
    public void IsDisposableDatabase_NeitherSuffix_ReturnsFalse()
    {
        var productionLikeDatabase = Generated.NewDatabaseName();

        Assert.False(CuratorTestDatabaseContract.IsDisposableDatabase(productionLikeDatabase));
    }

    [Fact]
    public void IsDisposableDatabase_TriageSuffixBeforeTheEnd_ReturnsFalse()
    {
        var triageMidNameDatabase = CuratorTestDatabaseContractConstants.TriageDatabaseNameSuffix + Generated.NewDatabaseName();

        Assert.False(CuratorTestDatabaseContract.IsDisposableDatabase(triageMidNameDatabase));
    }

    [Fact]
    public void IsDisposableDatabase_Null_ReturnsFalse()
    {
        Assert.False(CuratorTestDatabaseContract.IsDisposableDatabase(null));
    }
}
