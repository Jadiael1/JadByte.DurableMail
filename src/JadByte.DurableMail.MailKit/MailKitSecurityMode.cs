namespace JadByte.DurableMail.MailKit;

/// <summary>Specifies how the SMTP connection is secured.</summary>
public enum MailKitSecurityMode
{
    /// <summary>Lets MailKit select security based on the port; encryption is not guaranteed.</summary>
    Auto = 0,

    /// <summary>Requires upgrading the connection with STARTTLS.</summary>
    StartTls = 1,

    /// <summary>Uses STARTTLS when the server advertises support; encryption is not guaranteed.</summary>
    StartTlsWhenAvailable = 2,

    /// <summary>Establishes TLS immediately when connecting.</summary>
    SslOnConnect = 3,

    /// <summary>Disables TLS. Use only when explicitly appropriate for the deployment.</summary>
    None = 4,
}
