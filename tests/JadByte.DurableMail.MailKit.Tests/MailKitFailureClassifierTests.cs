using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Xunit;

namespace JadByte.DurableMail.MailKit.Tests;

public sealed class MailKitFailureClassifierTests
{
    [Theory]
    [InlineData(421, EmailFailureKind.Transient)]
    [InlineData(450, EmailFailureKind.Transient)]
    [InlineData(451, EmailFailureKind.Transient)]
    [InlineData(454, EmailFailureKind.Transient)]
    [InlineData(500, EmailFailureKind.Permanent)]
    [InlineData(535, EmailFailureKind.Permanent)]
    [InlineData(550, EmailFailureKind.Permanent)]
    [InlineData(554, EmailFailureKind.Permanent)]
    public void UsesStructuredStatusCodeRegardlessOfMessage(int code, EmailFailureKind expected)
    {
        var exception = new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, (SmtpStatusCode)code, "arbitrary text");
        Assert.Equal(expected, new MailKitFailureClassifier().Classify(exception));
    }

    [Theory]
    [InlineData(454, EmailFailureKind.Transient)]
    [InlineData(535, EmailFailureKind.Permanent)]
    public void PreservesStructuredAuthenticationResponse(int code, EmailFailureKind expected)
    {
        var response = new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, (SmtpStatusCode)code, "server response");
        var exception = new AuthenticationException("authentication failed", response);
        Assert.Equal(expected, new MailKitFailureClassifier().Classify(exception));
    }

    [Fact]
    public void ClassifiesNetworkFailuresAsTransient()
    {
        var classifier = new MailKitFailureClassifier();
        Assert.Equal(EmailFailureKind.Transient, classifier.Classify(new TimeoutException()));
        Assert.Equal(EmailFailureKind.Transient, classifier.Classify(new SocketException()));
        Assert.Equal(EmailFailureKind.Transient, classifier.Classify(new IOException()));
        Assert.Equal(EmailFailureKind.Transient, classifier.Classify(new SmtpProtocolException("protocol failed")));
    }

    [Fact]
    public void ClassifiesDefinitiveFailuresAsPermanent()
    {
        var classifier = new MailKitFailureClassifier();
        Assert.Equal(EmailFailureKind.Permanent, classifier.Classify(new AuthenticationException("authentication failed")));
        Assert.Equal(EmailFailureKind.Permanent, classifier.Classify(new FormatException()));
        Assert.Equal(EmailFailureKind.Permanent, classifier.Classify(new DurableMailConfigurationException()));
    }

    [Fact]
    public void UnknownFailureRemainsTransient()
    {
        Assert.Equal(EmailFailureKind.Transient, new MailKitFailureClassifier().Classify(new InvalidOperationException()));
    }
}
