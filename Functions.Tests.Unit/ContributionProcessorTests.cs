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
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson<ContributionPayload?>(null));
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
        var correctedOldValue = TestValues.NewFieldValue();
        var contributorId = TestValues.NewContributorId();
        var payload = new ContributionPayload(
            correctedChurchId,
            contributorId,
            TestValues.NewFieldName(),
            correctedOldValue,
            TestValues.NewFieldValue());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await processor.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var insert = Assert.Single(connection.ExecutedCommands);
        Assert.Contains("INSERT INTO [dbo].[UserCorrections]", insert.CommandText, StringComparison.Ordinal);
        Assert.Equal(correctedOldValue, insert.Parameters[ContributionProcessor.OldValueParameter].Value);
        Assert.Equal(contributorId, insert.Parameters["@UserId"].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenTheContributorIsNotAGuid_DeadLettersMessageWithoutDbAccess()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var processor = new ContributionProcessor(connection);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(new
            {
                ChurchId = Guid.NewGuid(),
                UserId = TestValues.NewFieldValue(),
                Field = TestValues.NewFieldName(),
                NewValue = TestValues.NewFieldValue(),
            }));
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
    public async Task Run_OldValueNullConnectionClosed_OpensAndInsertsDbNull()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var processor = new ContributionProcessor(connection);
        var correctedChurchId = Guid.NewGuid();
        var payload = new ContributionPayload(
            correctedChurchId,
            TestValues.NewContributorId(),
            TestValues.NewFieldName(),
            null,
            TestValues.NewFieldValue());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await processor.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
        var insert = Assert.Single(connection.ExecutedCommands);
        Assert.Equal(DBNull.Value, insert.Parameters[ContributionProcessor.OldValueParameter].Value);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }
}
