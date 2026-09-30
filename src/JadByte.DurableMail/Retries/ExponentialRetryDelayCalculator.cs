namespace JadByte.DurableMail.Retries;

internal interface IRetryDelayCalculator
{
    TimeSpan Calculate(int failedAttemptCount, EmailRetryOptions options);
}

internal sealed class ExponentialRetryDelayCalculator : IRetryDelayCalculator
{
    public TimeSpan Calculate(int failedAttemptCount, EmailRetryOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttemptCount, 1);
        var ticks = Math.Min(options.InitialDelay.Ticks * Math.Pow(options.BackoffFactor, failedAttemptCount - 1), options.MaxDelay.Ticks);
        ticks *= 1 + ((Random.Shared.NextDouble() * 2 - 1) * options.JitterRatio);
        return TimeSpan.FromTicks((long)Math.Clamp(ticks, 0, TimeSpan.MaxValue.Ticks - 1024d));
    }
}
