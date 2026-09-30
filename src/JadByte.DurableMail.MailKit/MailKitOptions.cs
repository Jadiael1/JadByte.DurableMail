namespace JadByte.DurableMail.MailKit;

/// <summary>Configures SMTP delivery through MailKit.</summary>
public sealed class MailKitOptions
{
    /// <summary>Gets or sets the SMTP server hostname or IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the SMTP port. The default is 587.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Gets or sets the optional authentication username.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the optional authentication password.</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets the sender email address.</summary>
    public string FromEmail { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional sender display name.</summary>
    public string? FromName { get; set; }

    /// <summary>Gets or sets the connection security mode. The default requires STARTTLS.</summary>
    public MailKitSecurityMode SecurityMode { get; set; } = MailKitSecurityMode.StartTls;

    /// <summary>Gets or sets the timeout for the complete SMTP operation. The default is 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
