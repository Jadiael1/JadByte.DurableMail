namespace JadByte.DurableMail;

/// <summary>Configures dispatch and retry behavior.</summary>
public sealed class DurableMailOptions
{
    /// <summary>Gets or sets local dispatch settings.</summary>
    public EmailDispatchOptions Dispatch { get; set; } = new();

    /// <summary>Gets or sets retry policy and trigger settings.</summary>
    public EmailRetryOptions Retries { get; set; } = new();
}

