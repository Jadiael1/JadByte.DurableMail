using Microsoft.Extensions.Options;

namespace JadByte.DurableMail;

internal sealed class DurableMailOptionsValidator : IValidateOptions<DurableMailOptions>
{
    public ValidateOptionsResult Validate(string? name, DurableMailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> errors = [];
        if (options.Dispatch is null)
        {
            errors.Add("Dispatch is required.");
        }
        else
        {
            if (options.Dispatch.BatchSize <= 0)
            {
                errors.Add("Dispatch.BatchSize must be greater than zero.");
            }

            if (options.Dispatch.MaxDegreeOfParallelism <= 0)
            {
                errors.Add("Dispatch.MaxDegreeOfParallelism must be greater than zero.");
            }

            if (options.Dispatch.LeaseDuration <= TimeSpan.Zero)
            {
                errors.Add("Dispatch.LeaseDuration must be greater than zero.");
            }
        }

        if (options.Retries is null)
        {
            errors.Add("Retries is required.");
        }
        else
        {
            EmailRetryOptions retry = options.Retries;
            if (retry.MaxAttempts < 1)
            {
                errors.Add("Retries.MaxAttempts must be at least one.");
            }

            if (retry.InitialDelay <= TimeSpan.Zero)
            {
                errors.Add("Retries.InitialDelay must be greater than zero.");
            }

            if (!double.IsFinite(retry.BackoffFactor) || retry.BackoffFactor < 1)
            {
                errors.Add("Retries.BackoffFactor must be finite and at least one.");
            }

            if (retry.MaxDelay < retry.InitialDelay)
            {
                errors.Add("Retries.MaxDelay must be at least InitialDelay.");
            }

            if (!double.IsFinite(retry.JitterRatio) || retry.JitterRatio < 0 || retry.JitterRatio >= 1)
            {
                errors.Add("Retries.JitterRatio must be finite and in the range [0, 1).");
            }

            if (retry.Scheduled is null || retry.Enqueue is null || retry.Startup is null || retry.Middleware is null)
            {
                errors.Add("All retry trigger option groups are required.");
            }
            else if (retry.Middleware.Enabled && retry.Middleware.Interval <= TimeSpan.Zero)
            {
                errors.Add("Retries.Middleware.Interval must be greater than zero when enabled.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

