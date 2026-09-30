namespace JadByte.DurableMail.Storage;

/// <summary>Provides durable, concurrency-safe persistence for deliveries and attempts.</summary>
public interface IEmailDeliveryStore
{
    /// <summary>Atomically applies deduplication and commits a delivery.</summary>
    /// <param name="request">The enqueue data and captured defaults.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    Task<EmailEnqueueResult> EnqueueAsync(EmailEnqueueStoreRequest request, CancellationToken cancellationToken = default);

    /// <summary>Finds a bounded set of eligible pending deliveries.</summary>
    /// <param name="now">The eligibility cutoff.</param>
    /// <param name="take">The maximum number of identifiers.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<Guid>> GetEligibleDeliveryIdsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default);

    /// <summary>Atomically claims an eligible delivery and creates its attempt.</summary>
    /// <param name="deliveryId">The candidate delivery identifier.</param>
    /// <param name="now">The claim time.</param>
    /// <param name="leaseDuration">The maximum ownership duration.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    /// <returns>A claim or null if another actor already changed the delivery.</returns>
    Task<ClaimedEmailDelivery?> TryClaimAsync(Guid deliveryId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>Records success only if the supplied claim still owns the delivery.</summary>
    /// <param name="claim">The ownership token and attempt.</param>
    /// <param name="now">The completion time.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    Task<bool> MarkSucceededAsync(ClaimedEmailDelivery claim, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Records a transient failure and schedules retry, or fails exhausted attempts.</summary>
    /// <param name="claim">The ownership token and attempt.</param>
    /// <param name="now">The failure time.</param>
    /// <param name="nextAttemptAt">The next eligible retry time.</param>
    /// <param name="failure">The sanitized failure details.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    Task<bool> MarkTransientFailureAsync(ClaimedEmailDelivery claim, DateTimeOffset now, DateTimeOffset nextAttemptAt, EmailFailureDetails failure, CancellationToken cancellationToken = default);

    /// <summary>Records terminal failure only if the claim still owns the delivery.</summary>
    /// <param name="claim">The ownership token and attempt.</param>
    /// <param name="now">The failure time.</param>
    /// <param name="failure">The sanitized failure details.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    Task<bool> MarkPermanentFailureAsync(ClaimedEmailDelivery claim, DateTimeOffset now, EmailFailureDetails failure, CancellationToken cancellationToken = default);

    /// <summary>Abandons expired attempts and restores retry eligibility when attempts remain.</summary>
    /// <param name="now">The recovery time.</param>
    /// <param name="retryDelay">Calculates delay from the completed attempt number.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, Func<int, TimeSpan> retryDelay, CancellationToken cancellationToken = default);

    /// <summary>Finds the earliest pending delivery, retry or lease expiration.</summary>
    /// <param name="now">The current time used for already eligible deliveries.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<DateTimeOffset?> GetNextWakeUpAtAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Deletes a bounded set of eligible terminal deliveries and their attempts.</summary>
    /// <param name="request">The exclusive retention cutoffs.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    Task<EmailMaintenanceResult> PruneAsync(EmailPruneRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The captured data for an isolated enqueue transaction.</summary>
/// <param name="Request">The producer request.</param>
/// <param name="MaxAttempts">The total permitted number of attempts.</param>
/// <param name="Now">The creation time.</param>
public sealed record EmailEnqueueStoreRequest(EmailEnqueueRequest Request, int MaxAttempts, DateTimeOffset Now);

/// <summary>The immutable message and ownership data for one claimed attempt.</summary>
/// <param name="DeliveryId">The delivery identifier.</param>
/// <param name="AttemptId">The persisted attempt identifier.</param>
/// <param name="LeaseId">The current ownership lease.</param>
/// <param name="Version">The optimistic concurrency token after claiming.</param>
/// <param name="AttemptNumber">The one-based attempt number.</param>
/// <param name="MaxAttempts">The total permitted number of attempts.</param>
/// <param name="Message">The message to send.</param>
public sealed record ClaimedEmailDelivery(Guid DeliveryId, Guid AttemptId, Guid LeaseId, Guid Version, int AttemptNumber, int MaxAttempts, EmailMessage Message);

/// <summary>Sanitized error diagnostics suitable for durable persistence.</summary>
/// <param name="ErrorType">The exception type or classification.</param>
/// <param name="ErrorMessage">An optional safe, bounded message.</param>
/// <param name="StackTrace">An optional bounded stack trace without payloads.</param>
public sealed record EmailFailureDetails(string ErrorType, string? ErrorMessage, string? StackTrace);
