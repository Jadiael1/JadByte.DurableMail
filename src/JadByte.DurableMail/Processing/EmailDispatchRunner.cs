using System.Transactions;
using JadByte.DurableMail.Retries;
using JadByte.DurableMail.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JadByte.DurableMail.Processing;

internal sealed class EmailDispatchRunner(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<DurableMailOptions> options,
    IRetryDelayCalculator delayCalculator,
    RetryScheduleCoordinator schedule,
    ILogger<EmailDispatchRunner>? logger = null)
{
    private readonly ILogger<EmailDispatchRunner> _logger = logger ?? NullLogger<EmailDispatchRunner>.Instance;

    public void ScheduleRecovery(int failedRuns, Action signal) =>
        schedule.Consider(TimestampMath.AddDelay(timeProvider.GetUtcNow(), delayCalculator.Calculate(failedRuns, options.Value.Retries)), signal);

    public async Task<EmailDispatchResult> RunAsync(IReadOnlyCollection<Guid> pendingIds, bool scan, Action signal, CancellationToken cancellationToken)
    {
        // Enqueue and dispatch are independent units of work, even under a consumer TransactionScope.
        using var transaction = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        var total = new EmailDispatchResult(0, 0, 0, 0);
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        total = Add(total, await ExecuteBatchAsync(pendingIds, signal, cancellationToken).ConfigureAwait(false));
        if (scan)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<Guid> ids;
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    ids = await scope.ServiceProvider.GetRequiredService<IEmailDeliveryStore>()
                        .GetEligibleDeliveryIdsAsync(timeProvider.GetUtcNow(), options.Value.Dispatch.BatchSize, cancellationToken).ConfigureAwait(false);
                }

                if (ids.Count == 0)
                {
                    break;
                }

                total = Add(total, await ExecuteBatchAsync(ids, signal, cancellationToken).ConfigureAwait(false));
            }
        }

        if (options.Value.Retries.Scheduled.Enabled)
        {
            var generation = schedule.GetGeneration();
            await using var scope = scopeFactory.CreateAsyncScope();
            var next = await scope.ServiceProvider.GetRequiredService<IEmailDeliveryStore>()
                .GetNextWakeUpAtAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            schedule.Reconcile(next, signal, generation);
        }

        return total;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var recovered = await scope.ServiceProvider.GetRequiredService<IEmailDeliveryStore>()
            .RecoverExpiredLeasesAsync(timeProvider.GetUtcNow(), count => delayCalculator.Calculate(count, options.Value.Retries), cancellationToken).ConfigureAwait(false);
        if (recovered != 0)
        {
            MailLog.Recovered(_logger, recovered);
        }
    }

    private async Task<EmailDispatchResult> ExecuteBatchAsync(IEnumerable<Guid> ids, Action signal, CancellationToken cancellationToken)
    {
        var claimed = 0;
        var succeeded = 0;
        var transient = 0;
        var permanent = 0;
        await Parallel.ForEachAsync(ids, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.Dispatch.MaxDegreeOfParallelism,
            CancellationToken = cancellationToken,
        }, async (id, token) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<EmailDeliveryExecutor>().ExecuteAsync(id, signal, token).ConfigureAwait(false);
            Interlocked.Add(ref claimed, result.Claimed);
            Interlocked.Add(ref succeeded, result.Succeeded);
            Interlocked.Add(ref transient, result.TransientFailures);
            Interlocked.Add(ref permanent, result.PermanentFailures);
        }).ConfigureAwait(false);
        return new(claimed, succeeded, transient, permanent);
    }

    internal static EmailDispatchResult Add(EmailDispatchResult left, EmailDispatchResult right) =>
        new(left.Claimed + right.Claimed, left.Succeeded + right.Succeeded,
            left.TransientFailures + right.TransientFailures, left.PermanentFailures + right.PermanentFailures);
}
