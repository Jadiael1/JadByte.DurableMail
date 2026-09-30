namespace JadByte.DurableMail;

/// <summary>Configures exponential backoff and independent retry triggers.</summary>
public sealed class EmailRetryOptions
{
    /// <summary>Gets or sets total attempts, including the initial attempt. Defaults to five.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Gets or sets the base delay after the first failure. Defaults to two minutes.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets the exponential growth factor. Defaults to two.</summary>
    public double BackoffFactor { get; set; } = 2;

    /// <summary>Gets or sets the base delay cap before jitter. Defaults to thirty minutes.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets or sets proportional random jitter in the range [0, 1). Defaults to 0.15.</summary>
    public double JitterRatio { get; set; } = 0.15;

    /// <summary>Gets or sets the enabled-by-default one-shot scheduling trigger.</summary>
    public ScheduledRetryTriggerOptions Scheduled { get; set; } = new();

    /// <summary>Gets or sets the opportunistic retry scan after enqueue.</summary>
    public EnqueueRetryTriggerOptions Enqueue { get; set; } = new();

    /// <summary>Gets or sets the optional HTTP middleware trigger.</summary>
    public MiddlewareRetryTriggerOptions Middleware { get; set; } = new();

    /// <summary>Gets or sets the optional one-time startup drain.</summary>
    public StartupRetryTriggerOptions Startup { get; set; } = new();
}

