namespace JadByte.DurableMail;

/// <summary>Configures one-shot scheduling and persisted schedule rehydration.</summary>
public sealed class ScheduledRetryTriggerOptions
{
    /// <summary>Gets or sets whether scheduled retry is enabled. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;
}

