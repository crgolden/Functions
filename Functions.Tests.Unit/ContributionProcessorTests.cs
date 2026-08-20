namespace Functions.Tests.Unit;

using System.Data;
using Azure.Messaging.ServiceBus;
using Churches;
using Churches.Moderation;
using Microsoft.Azure.Functions.Worker;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ContributionProcessorTests
{
    [Fact]
    public async Task Run_WhenPayloadIsNull_DeadLettersMessageWithoutDbAccess()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var processor = new ContributionProcessor(connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await processor.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(connection.ExecutedCommands);
        actions.Verify(
            a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_OldValuePresentConnectionOpen_InsertsValue()
    {
        // Arrange
        var connection = new FakeDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var processor = new ContributionProcessor(connection);
        var correctedChurchId = Guid.NewGuid();
        var correctedOldValue = NewFieldValue();
        var payload = new ContributionPayload(
            correctedChurchId,
            NewContributorId(),
            NewFieldName(),
            correctedOldValue,
            NewFieldValue());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await processor.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = Assert.Single(connection.ExecutedCommands);
        Assert.Contains("INSERT INTO [dbo].[UserCorrections]", insert.CommandText, StringComparison.Ordinal);
        Assert.Equal(correctedOldValue, insert.Parameters["@OldValue"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_OldValueNullConnectionClosed_OpensAndInsertsDbNull()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var processor = new ContributionProcessor(connection);
        var correctedChurchId = Guid.NewGuid();
        var payload = new ContributionPayload(
            correctedChurchId,
            NewContributorId(),
            NewFieldName(),
            null,
            NewFieldValue());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await processor.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        var insert = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(DBNull.Value, insert.Parameters["@OldValue"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string NewContributorId() => $"user{Guid.NewGuid():N}";

    private static string NewFieldName() => $"field{Guid.NewGuid():N}";

    private static string NewFieldValue() => $"value{Guid.NewGuid():N}";
}
