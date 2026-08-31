namespace Functions.Tests.Unit;

using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Moq;
using Notifications;
using Resend;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EmailTests
{
    private readonly Mock<IResend> _resendMock = new(MockBehavior.Strict);
    private readonly Mock<ServiceBusMessageActions> _actionsMock = new(MockBehavior.Strict);
    private readonly Email _email;

    public EmailTests()
    {
        _email = new Email(_resendMock.Object);
    }

    [Fact]
    public async Task Run_SendsEmailThenCompletesMessage()
    {
        // Arrange
        var sentMessageId = Guid.NewGuid();
        var htmlBody = NewHtmlBody();
        var subject = NewSubject();
        var recipientAddress = NewEmailAddress();
        var senderAddress = NewEmailAddress();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes(Encoding.UTF8.GetBytes(htmlBody)),
            subject: subject,
            to: recipientAddress,
            replyTo: senderAddress);

        _resendMock
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResendResponse<Guid>(sentMessageId, null));
        _actionsMock
            .Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _email.Run(message, _actionsMock.Object, CancellationToken.None);

        // Assert
        _resendMock.Verify(
            r => r.EmailSendAsync(
                It.Is<EmailMessage>(m =>
                    m.From != null && string.Equals(m.From.Email, senderAddress, StringComparison.Ordinal) &&
                    m.To.Any(a => string.Equals(a.Email, recipientAddress, StringComparison.Ordinal)) &&
                    string.Equals(m.Subject, subject, StringComparison.Ordinal) &&
                    string.Equals(m.HtmlBody, htmlBody, StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _actionsMock.Verify(
            a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WhenEmailSendAsyncThrows_DoesNotCompleteMessage()
    {
        // Arrange
        var sendFailureMessage = TestValues.NewErrorMessage();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes(Encoding.UTF8.GetBytes(NewHtmlBody())),
            subject: NewSubject(),
            to: NewEmailAddress(),
            replyTo: NewEmailAddress());

        _resendMock
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(sendFailureMessage));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _email.Run(message, _actionsMock.Object, CancellationToken.None));

        // Assert
        Assert.Equal(sendFailureMessage, exception.Message);
        _actionsMock.Verify(
            a => a.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_WhenBodyIsEmpty_SendsNullHtmlBody()
    {
        // Arrange
        var sentMessageId = Guid.NewGuid();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes([]),
            subject: NewSubject(),
            to: NewEmailAddress(),
            replyTo: NewEmailAddress());

        _resendMock
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResendResponse<Guid>(sentMessageId, null));
        _actionsMock
            .Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _email.Run(message, _actionsMock.Object, CancellationToken.None);

        // Assert
        _resendMock.Verify(
            r => r.EmailSendAsync(
                It.Is<EmailMessage>(m => m.HtmlBody == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static string NewEmailAddress() => TestValues.NewEmailAddress();

    private static string NewSubject() => $"Subject {Guid.NewGuid():N}";

    private static string NewHtmlBody() => $"<p>{Guid.NewGuid():N}</p>";
}
