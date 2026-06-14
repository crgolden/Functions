namespace Functions;

using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Resend;

public class Email
{
    private readonly IResend _resend;

    public Email(IResend resend)
    {
        _resend = resend;
    }

    [Function(nameof(Email))]
    public async Task Run(
        [ServiceBusTrigger("email", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken = default)
    {
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
