namespace JadByte.DurableMail;

/// <summary>Configures retry scanning as an additional opportunity on enqueue.</summary>
public sealed class EnqueueRetryTriggerOptions
{
    /// <summary>Gets or sets whether enqueue scans due retries. Defaults to false.</summary>
    public bool Enabled { get; set; }
}

