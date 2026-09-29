namespace Functions.Tests.Unit;

using Azure.Messaging.ServiceBus;
using Functions.Tests.Unit.TestSupport;
using Grpc.Core;

[Trait("Category", "Unit")]
public sealed class ServiceBusSettlementTests
{
    [Theory]
    [InlineData(ServiceBusFailureReason.SessionLockLost)]
    [InlineData(ServiceBusFailureReason.ServiceTimeout)]
    [InlineData(ServiceBusFailureReason.ServiceCommunicationProblem)]
    [InlineData(ServiceBusFailureReason.MessagingEntityNotFound)]
    public void IsLost_WhenTheHostWrapsAnyOtherFailureReason_ReturnsFalse(ServiceBusFailureReason reason)
    {
        // Arrange
        var fault = HostSettlementFaults.Wrapping(reason);

        // Act
        var lost = ServiceBusSettlement.IsLost(fault);

        // Assert
        Assert.False(lost);
    }

    [Theory]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public void IsLost_WhenTheStatusIsNeitherAThrownFaultNorAReleasedToken_ReturnsFalse(StatusCode statusCode)
    {
        // Arrange
        var fault = HostSettlementFaults.LockLostWithStatus(statusCode);

        // Act
        var lost = ServiceBusSettlement.IsLost(fault);

        // Assert
        Assert.False(lost);
    }

    [Fact]
    public void IsLost_WhenTheDetailIsNotAServiceBusException_ReturnsFalse()
    {
        // Arrange
        var lookalike = new InvalidOperationException(
            Generated.NewErrorMessage() + " (" + nameof(ServiceBusFailureReason.MessageLockLost) + ")");
        var fault = HostSettlementFaults.WithDetail(lookalike.ToString());

        // Act
        var lost = ServiceBusSettlement.IsLost(fault);

        // Assert
        Assert.False(lost);
    }

    [Fact]
    public void IsLost_WhenTheLockLostReasonAppearsOnlyAfterTheFirstLine_ReturnsFalse()
    {
        // Arrange
        var firstLine = new ServiceBusException(Generated.NewErrorMessage(), ServiceBusFailureReason.ServiceTimeout).ToString();
        var laterLine = HostSettlementFaults.LockLost().Status.Detail;
        var fault = HostSettlementFaults.WithDetail(firstLine + Environment.NewLine + laterLine);

        // Act
        var lost = ServiceBusSettlement.IsLost(fault);

        // Assert
        Assert.False(lost);
    }

    [Fact]
    public void IsLost_WhenTheHostWrapsAMessageLockLost_ReturnsTrue()
    {
        // Arrange
        var fault = HostSettlementFaults.LockLost();

        // Act
        var lost = ServiceBusSettlement.IsLost(fault);

        // Assert
        Assert.True(lost);
    }

    [Fact]
    public void IsLost_WhenTheHostWrapsAMessageLockLostThatNamesItsEntity_ReturnsTrue()
    {
        // Arrange
        var entityPath = Guid.NewGuid().ToString();
        var fault = HostSettlementFaults.WrappingOnEntity(ServiceBusFailureReason.MessageLockLost, entityPath);

        // Act
        var lost = ServiceBusSettlement.IsLost(fault);

        // Assert
        Assert.True(lost);
    }

    [Fact]
    public void IsLost_WhenTheWrappedLockLostCarriesAStackTrace_ReturnsTrue()
    {
        // Arrange
        var fault = HostSettlementFaults.LockLostWithStackTrace();

        // Act
        var lost = ServiceBusSettlement.IsLost(fault);

        // Assert
        Assert.True(lost);
    }

    [Fact]
    public void IsLost_WhenTheHostHasAlreadyReleasedTheLockToken_ReturnsTrue()
    {
        // Arrange
        var fault = HostSettlementFaults.LockTokenReleased();

        // Act
        var lost = ServiceBusSettlement.IsLost(fault);

        // Assert
        Assert.True(lost);
    }

    [Fact]
    public async Task SettleAsync_WhenTheSettlementIsLost_ReturnsWithoutThrowing()
    {
        // Arrange
        var lostEvent = Generated.NewErrorMessage();
        var fault = HostSettlementFaults.LockLost();

        // Act
        var exception = await Record.ExceptionAsync(() => ServiceBusSettlement.SettleAsync(() => Task.FromException(fault), lostEvent));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task SettleAsync_WhenTheSettlementFailsForAnyOtherReason_LetsItEscape()
    {
        // Arrange
        var lostEvent = Generated.NewErrorMessage();
        var brokerFault = HostSettlementFaults.Wrapping(ServiceBusFailureReason.ServiceCommunicationProblem);

        // Act
        var exception = await Record.ExceptionAsync(() => ServiceBusSettlement.SettleAsync(() => Task.FromException(brokerFault), lostEvent));

        // Assert
        Assert.Same(brokerFault, exception);
    }
}
