namespace Functions;

using System.Text;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Resend;

public class Email
{
    private readonly IResend _resend;
    private readonly ILogger<Email> _logger;

    public Email(IResend resend, ILogger<Email> logger)
    {
        _resend = resend;
        _logger = logger;
    }

    [Function(nameof(Email))]
    public async Task Run(
        [ServiceBusTrigger("email", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Received email message from {From}.", message.ReplyTo);
        var htmlBody = message.Body.Length > 0 ? Encoding.UTF8.GetString(message.Body) : null;
        var msg = new EmailMessage
        {
            From = message.ReplyTo,
            Subject = message.Subject,
            HtmlBody = htmlBody
        };
        msg.To.Add(message.To);
        await _resend.EmailSendAsync(msg, cancellationToken);
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }
}
