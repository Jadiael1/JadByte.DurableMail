using System.Transactions;
using JadByte.DurableMail.Retries;
using JadByte.DurableMail.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JadByte.DurableMail.Processing;

internal sealed class DurableMailLifecycleService(IServiceScopeFactory scopeFactory, IOptions<DurableMailOptions> options,
    TimeProvider timeProvider, EmailDispatchCoordinator coordinator, EmailDispatchRunner runner, RetryScheduleCoordinator schedule) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var transaction = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetService<IEmailDeliveryStore>()
            ?? throw new DurableMailConfigurationException("Register an email delivery store, for example UseEntityFrameworkCore<TContext>().");
        _ = scope.ServiceProvider.GetService<IEmailTransport>()
            ?? throw new DurableMailConfigurationException("Register IEmailTransport, for example UseMailKit().");
        if (options.Value.Retries.Scheduled.Enabled)
        {
            await runner.RecoverAsync(cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var next = await store.GetNextWakeUpAtAsync(now, cancellationToken).ConfigureAwait(false);
            if (next <= now)
            {
                coordinator.SignalRetries();
            }
            else
            {
                schedule.Consider(next, coordinator.SignalRetries);
            }
        }

        if (options.Value.Retries.Startup.Enabled)
        {
            coordinator.SignalRetries();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        schedule.Dispose();
        await coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
