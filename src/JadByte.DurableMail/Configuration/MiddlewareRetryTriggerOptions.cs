namespace JadByte.DurableMail;

/// <summary>Configures locally throttled retry signaling from HTTP requests.</summary>
public sealed class MiddlewareRetryTriggerOptions
{
    /// <summary>Gets or sets whether middleware signaling is enabled. Defaults to false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the minimum signal interval. Defaults to one minute.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}

