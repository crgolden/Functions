namespace Functions.Tests;

using System.Data;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ContributionProcessorTests
{
    [Fact]
    public async Task Run_WhenPayloadIsNull_CompletesWithoutDbAccess()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var processor = new ContributionProcessor(connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await processor.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_OldValuePresentConnectionOpen_InsertsValue()
    {
        // Arrange — connection already Open; OldValue present binds its value (left of ?? DBNull.Value)
        var connection = new FakeDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var processor = new ContributionProcessor(connection);
        var payload = new ContributionPayload(Guid.NewGuid(), "user-1", "name", "Old Name", "New Name");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await processor.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — one INSERT with OldValue bound to its value; message completed
        var insert = Assert.Single(connection.ExecutedCommands);
        Assert.Contains("INSERT INTO [dbo].[UserCorrections]", insert.CommandText, StringComparison.Ordinal);
        Assert.Equal("Old Name", insert.Parameters["@OldValue"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_OldValueNullConnectionClosed_OpensAndInsertsDbNull()
    {
        // Arrange — connection starts Closed; null OldValue coalesces to DBNull (right of ?? DBNull.Value)
        var connection = new FakeDbConnection();
        var processor = new ContributionProcessor(connection);
        var payload = new ContributionPayload(Guid.NewGuid(), "user-1", "name", null, "New Name");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await processor.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — connection opened; OldValue bound DBNull; message completed
        Assert.Equal(ConnectionState.Open, connection.State);
        var insert = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(DBNull.Value, insert.Parameters["@OldValue"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }
}
