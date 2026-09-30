using System.Transactions;
using JadByte.DurableMail.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JadByte.DurableMail.Processing;

internal sealed class EmailQueue(IServiceScopeFactory scopeFactory, EmailDispatchCoordinator coordinator,
    TimeProvider timeProvider, IOptions<DurableMailOptions> options, ILogger<EmailQueue>? logger = null) : IEmailQueue
{
    private readonly ILogger<EmailQueue> _logger = logger ?? NullLogger<EmailQueue>.Instance;

    public async Task<EmailEnqueueResult> EnqueueAsync(EmailEnqueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Message);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message.Recipient);
        ArgumentNullException.ThrowIfNull(request.Message.Subject);
        ArgumentNullException.ThrowIfNull(request.Message.Body);
        if (!Enum.IsDefined(request.Message.BodyFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown email body format.");
        }

        if (request.Source?.Length > 128 || request.DeduplicationKey?.Length > 256)
        {
            throw new ArgumentException("Source is limited to 128 characters and deduplication keys to 256 characters.", nameof(request));
        }

        if (request.DeduplicationKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DeduplicationKey);
        }

        if (request.Policy is { } policy && (!Enum.IsDefined(policy.SameSource) || !Enum.IsDefined(policy.DifferentSource)))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown conflict behavior.");
        }

        EmailEnqueueResult result;
        using (var transaction = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            result = await scope.ServiceProvider.GetRequiredService<IEmailDeliveryStore>().EnqueueAsync(
                new EmailEnqueueStoreRequest(request, options.Value.Retries.MaxAttempts, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }

        MailLog.Enqueued(_logger, result.DeliveryId, result.Outcome);
        if (options.Value.Dispatch.AutoDispatchOnEnqueue && result.Outcome != EmailEnqueueOutcome.Existing)
        {
            coordinator.SignalPending(result.DeliveryId);
        }

        if (options.Value.Retries.Enqueue.Enabled)
        {
            coordinator.SignalRetries();
        }

        return result;
    }
}
