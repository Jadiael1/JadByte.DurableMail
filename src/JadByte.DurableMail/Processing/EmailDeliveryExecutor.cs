using System.Diagnostics;
using JadByte.DurableMail.Retries;
using JadByte.DurableMail.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JadByte.DurableMail.Processing;

internal sealed class EmailDeliveryExecutor(
    IEmailDeliveryStore store,
    IEmailTransport transport,
    IEmailFailureClassifier classifier,
    TimeProvider timeProvider,
    IOptions<DurableMailOptions> options,
    IRetryDelayCalculator delayCalculator,
    RetryScheduleCoordinator schedule,
    ILogger<EmailDeliveryExecutor>? logger = null)
{
    private readonly ILogger<EmailDeliveryExecutor> _logger = logger ?? NullLogger<EmailDeliveryExecutor>.Instance;

    public async Task<EmailDispatchResult> ExecuteAsync(Guid deliveryId, Action signal, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var claim = await store.TryClaimAsync(deliveryId, now, options.Value.Dispatch.LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return new(0, 0, 0, 0);
        }

        schedule.Consider(TimestampMath.AddDelay(now, options.Value.Dispatch.LeaseDuration), signal);
        MailLog.Claimed(_logger, deliveryId, claim.AttemptNumber);
        Exception? failure = null;
        try
        {
            await transport.SendAsync(claim.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        now = timeProvider.GetUtcNow();
        if (failure is null)
        {
            var saved = await store.MarkSucceededAsync(claim, now, cancellationToken).ConfigureAwait(false);
            if (saved)
            {
                MailLog.Succeeded(_logger, deliveryId, claim.AttemptNumber);
            }

            return new(1, saved ? 1 : 0, 0, 0);
        }

        var kind = classifier.Classify(failure);
        var errorType = Truncate(failure.GetType().FullName ?? failure.GetType().Name, 512);
        // Exception messages are arbitrary transport input and may contain tokens or SMTP credentials.
        var details = new EmailFailureDetails(errorType, $"Transport reported a {kind} failure.", Truncate(new StackTrace(failure, false).ToString(), 16384));
        bool recorded;
        if (kind == EmailFailureKind.Permanent)
        {
            recorded = await store.MarkPermanentFailureAsync(claim, now, details, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var next = TimestampMath.AddDelay(now, delayCalculator.Calculate(claim.AttemptNumber, options.Value.Retries));
            recorded = await store.MarkTransientFailureAsync(claim, now, next, details, cancellationToken).ConfigureAwait(false);
            if (recorded && claim.AttemptNumber < claim.MaxAttempts)
            {
                schedule.Consider(next, signal);
                MailLog.RetryScheduled(_logger, deliveryId, next);
            }
            else if (recorded)
            {
                MailLog.Exhausted(_logger, deliveryId, claim.MaxAttempts);
            }
        }

        if (recorded)
        {
            MailLog.Failed(_logger, deliveryId, claim.AttemptNumber, kind, errorType);
        }

        return new(1, 0, recorded && kind == EmailFailureKind.Transient ? 1 : 0, recorded && kind == EmailFailureKind.Permanent ? 1 : 0);
    }

    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
