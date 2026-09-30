using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace JadByte.DurableMail.MailKit;

internal interface ISmtpSession : IDisposable
{
    int Timeout { set; }

    Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken);

    Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken);

    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

internal interface ISmtpSessionFactory
{
    ISmtpSession Create();
}

internal sealed class SmtpSessionFactory : ISmtpSessionFactory
{
    public ISmtpSession Create() => new SmtpSession();
}

internal sealed class SmtpSession : ISmtpSession
{
    private readonly SmtpClient _client = new();

    public int Timeout { set => _client.Timeout = value; }

    public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken) =>
        _client.ConnectAsync(host, port, security, cancellationToken);

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
        _client.AuthenticateAsync(username, password, cancellationToken);

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken) =>
        await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        _client.DisconnectAsync(true, cancellationToken);

    public void Dispose() => _client.Dispose();
}
