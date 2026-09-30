namespace JadByte.DurableMail;

/// <summary>Configures an optional one-time startup drain independent of rehydration.</summary>
public sealed class StartupRetryTriggerOptions
{
    /// <summary>Gets or sets whether the startup drain is enabled. Defaults to false.</summary>
    public bool Enabled { get; set; }
}

