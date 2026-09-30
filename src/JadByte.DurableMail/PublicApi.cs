namespace JadByte.DurableMail;

/// <summary>Identifies the format of an email body.</summary>
public enum EmailBodyFormat
{
    /// <summary>The body contains HTML.</summary>
    Html = 0,
    /// <summary>The body contains plain text.</summary>
    PlainText = 1,
}

/// <summary>An email to deliver through the configured transport.</summary>
/// <param name="Recipient">The recipient address.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Body">The potentially sensitive body.</param>
/// <param name="BodyFormat">The body format.</param>
public sealed record EmailMessage(string Recipient, string Subject, string Body, EmailBodyFormat BodyFormat = EmailBodyFormat.Html);

/// <summary>Specifies how an active delivery with the same key is handled.</summary>
public enum ActiveDeliveryConflictBehavior
{
    /// <summary>Keep the existing active delivery.</summary>
    KeepExisting = 0,
    /// <summary>Supersede the existing delivery and create a new one.</summary>
    SupersedeExisting = 1,
}

/// <summary>Controls conflicts for deliveries sharing a deduplication key.</summary>
public sealed class EmailEnqueuePolicy
{
    /// <summary>Gets the policy when both sources are equal.</summary>
    public ActiveDeliveryConflictBehavior SameSource { get; init; } = ActiveDeliveryConflictBehavior.KeepExisting;

    /// <summary>Gets the policy when the sources differ.</summary>
    public ActiveDeliveryConflictBehavior DifferentSource { get; init; } = ActiveDeliveryConflictBehavior.KeepExisting;
}

/// <summary>Requests durable delivery of an email.</summary>
/// <param name="Message">The message to persist.</param>
/// <param name="Source">Optional semantic origin supplied by the producer.</param>
/// <param name="DeduplicationKey">Optional key identifying one logical active delivery.</param>
/// <param name="Policy">The conflict policy, defaulting to keeping the active delivery.</param>
public sealed record EmailEnqueueRequest(EmailMessage Message, string? Source = null, string? DeduplicationKey = null, EmailEnqueuePolicy? Policy = null);

/// <summary>Describes the durable result of an enqueue operation.</summary>
public enum EmailEnqueueOutcome
{
    /// <summary>A delivery was created.</summary>
    Created = 0,
    /// <summary>The existing delivery was retained.</summary>
    Existing = 1,
    /// <summary>The existing delivery was superseded and a new delivery was created.</summary>
    SupersededAndCreated = 2,
}

/// <summary>The committed result of an enqueue operation.</summary>
/// <param name="DeliveryId">The new or retained delivery identifier.</param>
/// <param name="Outcome">The operation outcome.</param>
public sealed record EmailEnqueueResult(Guid DeliveryId, EmailEnqueueOutcome Outcome);

/// <summary>Persists email deliveries without awaiting the external transport.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The public API intentionally describes a durable email queue, not a collection.")]
public interface IEmailQueue
{
    /// <summary>Commits an email delivery and signals configured processing after commit.</summary>
    /// <param name="request">The message, source, key and conflict policy.</param>
    /// <param name="cancellationToken">Cancels the persistence operation.</param>
    /// <returns>The committed enqueue result.</returns>
    Task<EmailEnqueueResult> EnqueueAsync(EmailEnqueueRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Delivers an email to an external system.</summary>
public interface IEmailTransport
{
    /// <summary>Sends an email. Implementations must observe cancellation.</summary>
    /// <param name="message">The email to send.</param>
    /// <param name="cancellationToken">Cancels transport work.</param>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Indicates whether another transport attempt may succeed.</summary>
public enum EmailFailureKind
{
    /// <summary>The failure can be retried within the configured attempt limit.</summary>
    Transient = 0,
    /// <summary>The failure should not be retried.</summary>
    Permanent = 1,
}

/// <summary>Classifies transport errors without exposing provider details to the dispatcher.</summary>
public interface IEmailFailureClassifier
{
    /// <summary>Classifies the specified transport exception.</summary>
    /// <param name="exception">The transport failure.</param>
    /// <returns>The retry disposition.</returns>
    EmailFailureKind Classify(Exception exception);
}

/// <summary>Runs a manual drain of currently eligible persistent work.</summary>
public interface IEmailRetryDispatcher
{
    /// <summary>Signals and awaits eligible delivery processing.</summary>
    /// <param name="cancellationToken">Cancels waiting for the dispatch operation.</param>
    /// <returns>Counters for the dispatch run.</returns>
    Task<EmailDispatchResult> DispatchDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>Summarizes attempts completed during a dispatch run.</summary>
/// <param name="Claimed">The number of successful claims.</param>
/// <param name="Succeeded">The number of successful deliveries.</param>
/// <param name="TransientFailures">The number of transient transport failures.</param>
/// <param name="PermanentFailures">The number of permanent transport failures.</param>
public sealed record EmailDispatchResult(int Claimed, int Succeeded, int TransientFailures, int PermanentFailures);

/// <summary>Defines exclusive terminal-state retention cutoffs for a bounded prune operation.</summary>
/// <param name="SucceededBefore">Prune deliveries succeeded before this time; null retains them.</param>
/// <param name="FailedBefore">Prune deliveries failed before this time; null retains them.</param>
/// <param name="SupersededBefore">Prune deliveries superseded before this time; null retains them.</param>
/// <param name="BatchSize">The maximum number of deliveries to delete.</param>
public sealed record EmailPruneRequest(DateTimeOffset? SucceededBefore = null, DateTimeOffset? FailedBefore = null, DateTimeOffset? SupersededBefore = null, int BatchSize = 100);

/// <summary>Summarizes a completed prune operation.</summary>
/// <param name="DeliveriesDeleted">The number of deleted deliveries.</param>
/// <param name="AttemptsDeleted">The number of deleted attempt records.</param>
public sealed record EmailMaintenanceResult(int DeliveriesDeleted, int AttemptsDeleted);

/// <summary>Provides explicit retention maintenance without an automatic cleanup worker.</summary>
public interface IEmailDeliveryMaintenance
{
    /// <summary>Deletes a bounded set of eligible terminal deliveries and their attempts.</summary>
    /// <param name="request">The retention cutoffs and batch limit.</param>
    /// <param name="cancellationToken">Cancels maintenance.</param>
    /// <returns>The deletion counts.</returns>
    Task<EmailMaintenanceResult> PruneAsync(EmailPruneRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The base exception for DurableMail-specific failures.</summary>
public class DurableMailException : Exception
{
    /// <summary>Initializes an exception with the default message.</summary>
    public DurableMailException() { }
    /// <summary>Initializes an exception with a safe diagnostic message.</summary>
    /// <param name="message">A message without credentials or email payloads.</param>
    public DurableMailException(string? message) : base(message) { }
    /// <summary>Initializes an exception with a safe message and underlying cause.</summary>
    /// <param name="message">A message without credentials or email payloads.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DurableMailException(string? message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>Indicates an invalid DurableMail configuration.</summary>
public sealed class DurableMailConfigurationException : DurableMailException
{
    /// <summary>Initializes an exception with the default message.</summary>
    public DurableMailConfigurationException() { }
    /// <summary>Initializes an exception with a configuration error message.</summary>
    /// <param name="message">A message without secret configuration values.</param>
    public DurableMailConfigurationException(string? message) : base(message) { }
    /// <summary>Initializes an exception with a message and underlying cause.</summary>
    /// <param name="message">A message without secret configuration values.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DurableMailConfigurationException(string? message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>Indicates failure in an external email transport.</summary>
public class EmailTransportException : DurableMailException
{
    /// <summary>Initializes an exception with the default message.</summary>
    public EmailTransportException() { }
    /// <summary>Initializes an exception with a transport error message.</summary>
    /// <param name="message">A message without credentials or email payloads.</param>
    public EmailTransportException(string? message) : base(message) { }
    /// <summary>Initializes an exception with a message and underlying cause.</summary>
    /// <param name="message">A message without credentials or email payloads.</param>
    /// <param name="innerException">The underlying cause.</param>
    public EmailTransportException(string? message, Exception? innerException) : base(message, innerException) { }
}
