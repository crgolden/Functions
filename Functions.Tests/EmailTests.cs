namespace Functions.Tests;

using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using Resend;

[Trait("Category", "Unit")]
public sealed class EmailTests
{
    private readonly Mock<IResend> _resendMock = new(MockBehavior.Strict);
    private readonly Mock<ServiceBusMessageActions> _actionsMock = new(MockBehavior.Strict);
    private readonly Email _email;

    public EmailTests()
    {
        _email = new Email(_resendMock.Object, Mock.Of<ILogger<Email>>());
    }

    [Fact]
    public async Task Run_SendsEmailThenCompletesMessage()
    {
        const string htmlBody = "<p>Confirm your email</p>";
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes(Encoding.UTF8.GetBytes(htmlBody)),
            subject: "Confirm your email",
            to: "user@example.com",
            replyTo: "noreply@crgolden.com");

        _resendMock
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResendResponse<Guid>(Guid.NewGuid(), null));
        _actionsMock
            .Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _email.Run(message, _actionsMock.Object, CancellationToken.None);

        _resendMock.Verify(
            r => r.EmailSendAsync(
                It.Is<EmailMessage>(m =>
                    m.From.Email == "noreply@crgolden.com" &&
                    m.To.Any(a => a.Email == "user@example.com") &&
                    m.Subject == "Confirm your email" &&
                    m.HtmlBody == htmlBody),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _actionsMock.Verify(
            a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WhenEmailSendAsyncThrows_DoesNotCompleteMessage()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes(Encoding.UTF8.GetBytes("<p>body</p>")),
            subject: "Subject",
            to: "user@example.com",
            replyTo: "noreply@crgolden.com");

        _resendMock
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Resend API error"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _email.Run(message, _actionsMock.Object, CancellationToken.None));

        _actionsMock.Verify(
            a => a.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_WhenBodyIsEmpty_SendsNullHtmlBody()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes([]),
            subject: "Subject",
            to: "user@example.com",
            replyTo: "noreply@crgolden.com");

        _resendMock
            .Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResendResponse<Guid>(Guid.NewGuid(), null));
        _actionsMock
            .Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _email.Run(message, _actionsMock.Object, CancellationToken.None);

        _resendMock.Verify(
            r => r.EmailSendAsync(
                It.Is<EmailMessage>(m => m.HtmlBody == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
