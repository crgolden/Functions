namespace Functions.Tests.Unit;

using Functions.Churches.Moderation;

[Trait("Category", "Unit")]
public sealed class DuplicateClustersTests
{
    private const int FirstIndex = 0;
    private const int SecondIndex = 1;
    private const int ThirdIndex = SecondIndex + 1;
    private const int FourthIndex = ThirdIndex + 1;
    private const int ChainLength = ThirdIndex + 1;
    private const int TwoPairsLength = FourthIndex + 1;

    [Fact]
    public void TryJoin_IndexesAlreadyLinkedThroughAThird_RefusesTheRedundantLink()
    {
        // Arrange
        var clusters = new DuplicateClusters(ChainLength);

        // Act
        var joinedFirstToSecond = clusters.TryJoin(FirstIndex, SecondIndex);
        var joinedSecondToThird = clusters.TryJoin(SecondIndex, ThirdIndex);
        var joinedFirstToThird = clusters.TryJoin(FirstIndex, ThirdIndex);

        // Assert
        Assert.True(joinedFirstToSecond);
        Assert.True(joinedSecondToThird);
        Assert.False(joinedFirstToThird);
    }

    [Fact]
    public void TryJoin_IndexesInAnUntouchedCluster_StillJoin()
    {
        // Arrange
        var clusters = new DuplicateClusters(TwoPairsLength);

        // Act
        var joinedFirstPair = clusters.TryJoin(FirstIndex, SecondIndex);
        var joinedSecondPair = clusters.TryJoin(ThirdIndex, FourthIndex);

        // Assert
        Assert.True(joinedFirstPair);
        Assert.True(joinedSecondPair);
    }
}
