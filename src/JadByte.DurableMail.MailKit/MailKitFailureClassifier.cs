using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace JadByte.DurableMail.MailKit;

internal sealed class MailKitFailureClassifier : IEmailFailureClassifier
{
    public EmailFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            MailKitTransportException failure => failure.FailureKind,
            SmtpCommandException command when (int)command.StatusCode is >= 400 and < 500 => EmailFailureKind.Transient,
            SmtpCommandException command when (int)command.StatusCode is >= 500 and < 600 => EmailFailureKind.Permanent,
            AuthenticationException { InnerException: SmtpCommandException command } => Classify(command),
            AuthenticationException => EmailFailureKind.Permanent,
            System.Security.Authentication.AuthenticationException => EmailFailureKind.Permanent,
            global::MailKit.ServiceNotAuthenticatedException or NotSupportedException => EmailFailureKind.Permanent,
            ParseException or FormatException or ArgumentException => EmailFailureKind.Permanent,
            DurableMailConfigurationException or OptionsValidationException => EmailFailureKind.Permanent,
            TimeoutException or IOException or SocketException => EmailFailureKind.Transient,
            _ => EmailFailureKind.Transient,
        };
    }
}

internal sealed class MailKitTransportException(string message, EmailFailureKind failureKind)
    : EmailTransportException(message)
{
    internal EmailFailureKind FailureKind { get; } = failureKind;
}
