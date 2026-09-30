using Microsoft.Extensions.Logging;

namespace JadByte.DurableMail.Processing;

internal static partial class MailLog
{
    [LoggerMessage(1, LogLevel.Information, "Delivery {DeliveryId} enqueue outcome: {Outcome}")]
    public static partial void Enqueued(ILogger logger, Guid deliveryId, EmailEnqueueOutcome outcome);

    [LoggerMessage(2, LogLevel.Debug, "Dispatcher started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(3, LogLevel.Debug, "Dispatcher stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(4, LogLevel.Error, "Dispatcher interrupted by {ErrorType}; persisted leases allow recovery")]
    public static partial void DispatchFailed(ILogger logger, string errorType);

    [LoggerMessage(5, LogLevel.Debug, "Delivery {DeliveryId} claimed; attempt {AttemptNumber} started")]
    public static partial void Claimed(ILogger logger, Guid deliveryId, int attemptNumber);

    [LoggerMessage(6, LogLevel.Information, "Delivery {DeliveryId} attempt {AttemptNumber} succeeded")]
    public static partial void Succeeded(ILogger logger, Guid deliveryId, int attemptNumber);

    [LoggerMessage(7, LogLevel.Warning, "Delivery {DeliveryId} attempt {AttemptNumber} failed: {FailureKind} ({ErrorType})")]
    public static partial void Failed(ILogger logger, Guid deliveryId, int attemptNumber, EmailFailureKind failureKind, string errorType);

    [LoggerMessage(8, LogLevel.Debug, "One-shot timer armed for {DueAt}")]
    public static partial void TimerArmed(ILogger logger, DateTimeOffset dueAt);

    [LoggerMessage(9, LogLevel.Information, "Recovered {Count} expired leases")]
    public static partial void Recovered(ILogger logger, int count);

    [LoggerMessage(10, LogLevel.Information, "Delivery {DeliveryId} retry scheduled for {DueAt}")]
    public static partial void RetryScheduled(ILogger logger, Guid deliveryId, DateTimeOffset dueAt);

    [LoggerMessage(11, LogLevel.Warning, "Delivery {DeliveryId} exhausted its {MaxAttempts} attempts")]
    public static partial void Exhausted(ILogger logger, Guid deliveryId, int maxAttempts);
}
