using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MimeKit;
using Xunit;

namespace JadByte.DurableMail.MailKit.Tests;

public sealed class MailKitEmailTransportTests
{
    [Theory]
    [InlineData(MailKitSecurityMode.Auto, SecureSocketOptions.Auto)]
    [InlineData(MailKitSecurityMode.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(MailKitSecurityMode.StartTlsWhenAvailable, SecureSocketOptions.StartTlsWhenAvailable)]
    [InlineData(MailKitSecurityMode.SslOnConnect, SecureSocketOptions.SslOnConnect)]
    [InlineData(MailKitSecurityMode.None, SecureSocketOptions.None)]
    public void MapsEverySecurityMode(MailKitSecurityMode mode, SecureSocketOptions expected)
    {
        Assert.Equal(expected, MailKitEmailTransport.MapSecurityMode(mode));
    }

    [Theory]
    [InlineData(EmailBodyFormat.Html, "html")]
    [InlineData(EmailBodyFormat.PlainText, "plain")]
    public void CreatesMimeBodyWithRequestedContentType(EmailBodyFormat format, string subtype)
    {
        using var message = MailKitEmailTransport.CreateMessage(new("recipient@example.com", "Subject", "content", format), MailKitOptionsTests.ValidOptions());
        var body = Assert.IsType<TextPart>(message.Body);
        Assert.Equal(subtype, body.ContentType.MediaSubtype);
        Assert.Equal("content", body.Text);
        Assert.Equal("Subject", message.Subject);
        Assert.Equal("recipient@example.com", Assert.IsType<MailboxAddress>(Assert.Single(message.To)).Address);
        Assert.Equal("sender@example.com", Assert.IsType<MailboxAddress>(Assert.Single(message.From)).Address);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Sender name", "Sender name")]
    public void SupportsOptionalSenderDisplayName(string? name, string expected)
    {
        var options = MailKitOptionsTests.ValidOptions();
        options.FromName = name;
        using var message = MailKitEmailTransport.CreateMessage(new("recipient@example.com", "Subject", "content"), options);
        Assert.Equal(expected, Assert.IsType<MailboxAddress>(Assert.Single(message.From)).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendsUsingIndependentSessionsAndOptionalAuthentication(bool authenticate)
    {
        var options = MailKitOptionsTests.ValidOptions();
        if (authenticate)
        {
            options.Username = "account";
            options.Password = "secret";
        }

        var factory = new FakeSessionFactory();
        var transport = new MailKitEmailTransport(Options.Create(options), new FakeTimeProvider(), factory);
        await transport.SendAsync(new("recipient@example.com", "Subject", "content"), TestContext.Current.CancellationToken);
        await transport.SendAsync(new("recipient@example.com", "Subject", "content"), TestContext.Current.CancellationToken);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.All(factory.Sessions, session =>
        {
            Assert.Equal(30000, session.Timeout);
            Assert.Equal("smtp.example.com", session.Host);
            Assert.Equal(587, session.Port);
            Assert.Equal(SecureSocketOptions.StartTls, session.Security);
            Assert.Equal(authenticate, session.Authenticated);
            Assert.True(session.Sent);
            Assert.True(session.Disconnected);
            Assert.True(session.Disposed);
        });
    }

    [Fact]
    public async Task TimeoutCoversTheWholeOperationAndIsTransient()
    {
        var clock = new FakeTimeProvider();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory
        {
            Configure = session =>
            {
                session.Connect = async token =>
                {
                    connected.SetResult();
                    await Task.Delay(TimeSpan.FromSeconds(20), clock, token);
                };
                session.Send = token =>
                {
                    sending.SetResult();
                    return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                };
            },
        };
        var transport = new MailKitEmailTransport(Options.Create(MailKitOptionsTests.ValidOptions()), clock, factory);
        var send = transport.SendAsync(new("recipient@example.com", "Subject", "content"), TestContext.Current.CancellationToken);
        await connected.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(20));
        await sending.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));
        var exception = await Assert.ThrowsAsync<MailKitTransportException>(() => send);
        Assert.Equal(EmailFailureKind.Transient, new MailKitFailureClassifier().Classify(exception));
        Assert.True(factory.Sessions[0].Disposed);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new FakeSessionFactory
        {
            Configure = session => session.Send = token => Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token),
        };
        var transport = new MailKitEmailTransport(Options.Create(MailKitOptionsTests.ValidOptions()), new FakeTimeProvider(), factory);
        var send = transport.SendAsync(new("recipient@example.com", "Subject", "content"), cancellation.Token);
        await cancellation.CancelAsync();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => send);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task SanitizesServerDiagnosticsAndRetainsFailureClassification()
    {
        var factory = new FakeSessionFactory
        {
            Configure = session => session.Send = _ => throw new SmtpCommandException(
                SmtpErrorCode.RecipientNotAccepted, (SmtpStatusCode)550, "password=super-secret; body=private-token"),
        };
        var transport = new MailKitEmailTransport(Options.Create(MailKitOptionsTests.ValidOptions()), new FakeTimeProvider(), factory);
        var exception = await Assert.ThrowsAsync<MailKitTransportException>(() =>
            transport.SendAsync(new("recipient@example.com", "Subject", "content"), TestContext.Current.CancellationToken));
        Assert.Equal(EmailFailureKind.Permanent, new MailKitFailureClassifier().Classify(exception));
        Assert.DoesNotContain("super-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task FailedQuitAfterAcceptanceDoesNotCauseRetry()
    {
        var factory = new FakeSessionFactory
        {
            Configure = session => session.Disconnect = _ => throw new IOException("connection closed"),
        };
        var transport = new MailKitEmailTransport(Options.Create(MailKitOptionsTests.ValidOptions()), new FakeTimeProvider(), factory);
        await transport.SendAsync(new("recipient@example.com", "Subject", "content"), TestContext.Current.CancellationToken);
        Assert.True(factory.Sessions[0].Sent);
        Assert.True(factory.Sessions[0].Disposed);
    }

    [Fact]
    public async Task InvalidRecipientFailsPermanentlyBeforeOpeningConnection()
    {
        var factory = new FakeSessionFactory();
        var transport = new MailKitEmailTransport(Options.Create(MailKitOptionsTests.ValidOptions()), new FakeTimeProvider(), factory);
        var exception = await Assert.ThrowsAsync<MailKitTransportException>(() =>
            transport.SendAsync(new("not a valid <", "Subject", "content"), TestContext.Current.CancellationToken));
        Assert.Equal(EmailFailureKind.Permanent, exception.FailureKind);
        Assert.Empty(factory.Sessions);
    }

    private sealed class FakeSessionFactory : ISmtpSessionFactory
    {
        internal List<FakeSession> Sessions { get; } = [];
        internal Action<FakeSession>? Configure { get; init; }

        public ISmtpSession Create()
        {
            var session = new FakeSession();
            Configure?.Invoke(session);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeSession : ISmtpSession
    {
        public int Timeout { get; set; }
        internal string? Host { get; private set; }
        internal int Port { get; private set; }
        internal SecureSocketOptions Security { get; private set; }
        internal bool Authenticated { get; private set; }
        internal bool Sent { get; private set; }
        internal bool Disconnected { get; private set; }
        internal bool Disposed { get; private set; }
        internal Func<CancellationToken, Task>? Connect { get; set; }
        internal Func<CancellationToken, Task>? Send { get; set; }
        internal Func<CancellationToken, Task>? Disconnect { get; set; }

        public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken)
        {
            Host = host;
            Port = port;
            Security = security;
            return Connect?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
        {
            Authenticated = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            Sent = true;
            return Send?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Disconnected = true;
            return Disconnect?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }
}
