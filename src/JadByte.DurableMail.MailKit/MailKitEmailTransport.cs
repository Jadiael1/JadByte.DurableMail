using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace JadByte.DurableMail.MailKit;

internal sealed class MailKitEmailTransport(
    IOptions<MailKitOptions> options,
    TimeProvider timeProvider,
    ISmtpSessionFactory sessionFactory) : IEmailTransport
{
    private readonly MailKitOptions _options = options.Value;
    private readonly MailKitFailureClassifier _classifier = new();

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(_options.Timeout, timeProvider);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var mimeMessage = CreateMessage(message, _options);
            using var session = sessionFactory.Create();
            session.Timeout = (int)Math.Ceiling(_options.Timeout.TotalMilliseconds);
            await session.ConnectAsync(_options.Host, _options.Port, MapSecurityMode(_options.SecurityMode), operation.Token)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(_options.Username))
            {
                await session.AuthenticateAsync(_options.Username, _options.Password!, operation.Token).ConfigureAwait(false);
            }

            await session.SendAsync(mimeMessage, operation.Token).ConfigureAwait(false);

            try
            {
                await session.DisconnectAsync(operation.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The server already accepted the message. A failed QUIT must not cause duplicate delivery.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("SMTP delivery was canceled.", cancellationToken);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new MailKitTransportException("The SMTP operation timed out.", EmailFailureKind.Transient);
        }
        catch (Exception exception)
        {
            // Server diagnostics can contain credentials or message content; preserve only classification.
            throw new MailKitTransportException("SMTP delivery failed.", _classifier.Classify(exception));
        }
    }

    internal static MimeMessage CreateMessage(EmailMessage message, MailKitOptions options)
    {
        if (string.IsNullOrWhiteSpace(message.Recipient) ||
            message.Recipient.Contains('\r', StringComparison.Ordinal) ||
            message.Recipient.Contains('\n', StringComparison.Ordinal) ||
            !MailboxAddress.TryParse(message.Recipient, out var recipient))
        {
            throw new FormatException("The recipient must be a valid mailbox address.");
        }

        var sender = MailboxAddress.Parse(options.FromEmail);
        if (options.FromName is not null)
        {
            sender.Name = options.FromName;
        }

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(sender);
        mimeMessage.To.Add(recipient);
        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = message.BodyFormat switch
        {
            EmailBodyFormat.Html => new TextPart("html") { Text = message.Body },
            EmailBodyFormat.PlainText => new TextPart("plain") { Text = message.Body },
            _ => throw new ArgumentException("The email body format is not supported.", nameof(message)),
        };
        return mimeMessage;
    }

    internal static SecureSocketOptions MapSecurityMode(MailKitSecurityMode securityMode) => securityMode switch
    {
        MailKitSecurityMode.Auto => SecureSocketOptions.Auto,
        MailKitSecurityMode.StartTls => SecureSocketOptions.StartTls,
        MailKitSecurityMode.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        MailKitSecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailKitSecurityMode.None => SecureSocketOptions.None,
        _ => throw new DurableMailConfigurationException("The SMTP security mode is invalid."),
    };
}
