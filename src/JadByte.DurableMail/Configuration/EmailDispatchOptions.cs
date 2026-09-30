namespace JadByte.DurableMail;

/// <summary>Configures the bounded, run-to-completion dispatcher.</summary>
public sealed class EmailDispatchOptions
{
    /// <summary>Gets or sets whether committed new deliveries are signaled immediately.</summary>
    public bool AutoDispatchOnEnqueue { get; set; } = true;

    /// <summary>Gets or sets the maximum candidates fetched in one batch. Defaults to 25.</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>Gets or sets the maximum concurrent transports. Defaults to one.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>Gets or sets claim ownership duration. Defaults to two minutes.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
}

