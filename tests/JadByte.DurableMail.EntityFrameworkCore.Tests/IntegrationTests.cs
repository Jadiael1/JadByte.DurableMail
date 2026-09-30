using System.Transactions;
using JadByte.DurableMail.EntityFrameworkCore.Persistence.Entities;
using JadByte.DurableMail.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace JadByte.DurableMail.EntityFrameworkCore.Tests;

public sealed partial class StoreTests
{
    [Fact]
    public async Task QueueUsesIndependentScopeAndCommitsOutsideTheConsumersAmbientTransaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new FakeTimeProvider(Now);
        var transport = new RecordingTransport();
        await using var provider = CreateProvider(database, clock, transport, scheduled: false);
        await using var request = provider.CreateAsyncScope();
        var businessContext = request.ServiceProvider.GetRequiredService<MailContext>();
        businessContext.Add(new BusinessEntity { Id = Guid.NewGuid() });
        EmailEnqueueResult result;
        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var original = Transaction.Current;
            result = await request.ServiceProvider.GetRequiredService<IEmailQueue>()
                .EnqueueAsync(Request("independent").Request, TestContext.Current.CancellationToken);
            Assert.Same(original, Transaction.Current);
            Assert.Equal(EntityState.Added, Assert.Single(businessContext.ChangeTracker.Entries()).State);
            // Deliberately roll back the consumer transaction; the durable enqueue has already committed.
        }

        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<BusinessEntity>().AnyAsync(TestContext.Current.CancellationToken));
        Assert.True(await verify.Set<EmailDeliveryEntity>().AnyAsync(x => x.Id == result.DeliveryId, TestContext.Current.CancellationToken));
        var dispatch = await provider.GetRequiredService<IEmailRetryDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, dispatch.Succeeded);
        Assert.Equal(1, transport.Count);
        Assert.False(transport.SawAmbientTransaction);
        var attempt = await verify.Set<EmailDeliveryAttemptEntity>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmailAttemptOutcome.Succeeded, attempt.Outcome);
    }

    [Fact]
    public async Task RestartRehydratesFuturePersistedRetryWithoutStartupTriggerOrManualDispatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var due = Now.AddMinutes(5);
        await using (var previousContext = database.CreateContext())
        {
            var previousStore = new EfEmailDeliveryStore<MailContext>(previousContext);
            var delivery = await previousStore.EnqueueAsync(Request(), TestContext.Current.CancellationToken);
            var claim = Assert.IsType<ClaimedEmailDelivery>(await previousStore.TryClaimAsync(delivery.DeliveryId, Now, Lease, TestContext.Current.CancellationToken));
            await previousStore.MarkTransientFailureAsync(claim, Now.AddSeconds(1), due, Failure, TestContext.Current.CancellationToken);
        }

        var clock = new FakeTimeProvider(Now.AddMinutes(1));
        var transport = new RecordingTransport();
        await using var provider = CreateProvider(database, clock, transport, scheduled: true);
        var lifecycle = provider.GetServices<IHostedService>().Single();
        await lifecycle.StartAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(4) - TimeSpan.FromTicks(1));
        Assert.Equal(0, transport.Count);
        clock.Advance(TimeSpan.FromTicks(1));
        await transport.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await lifecycle.StopAsync(TestContext.Current.CancellationToken);
        await using var verify = database.CreateContext();
        Assert.Equal(EmailDeliveryStatus.Succeeded, (await verify.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal([EmailAttemptOutcome.TransientFailure, EmailAttemptOutcome.Succeeded], await verify.Set<EmailDeliveryAttemptEntity>()
            .OrderBy(x => x.AttemptNumber).Select(x => x.Outcome).ToArrayAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task RestartArmsFutureLeaseExpiryAndRecoversAbandonedAttemptWithoutExternalTrigger()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var previousContext = database.CreateContext())
        {
            var previousStore = new EfEmailDeliveryStore<MailContext>(previousContext);
            var delivery = await previousStore.EnqueueAsync(Request("leased"), TestContext.Current.CancellationToken);
            await previousStore.TryClaimAsync(delivery.DeliveryId, Now, Lease, TestContext.Current.CancellationToken);
        }

        var clock = new FakeTimeProvider(Now.AddMinutes(1));
        var transport = new RecordingTransport();
        var recovered = new RecoveryObserverInterceptor();
        await using var provider = CreateProvider(database, clock, transport, scheduled: true, recovered);
        var lifecycle = provider.GetServices<IHostedService>().Single();
        await lifecycle.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(recovered.Saved.Task.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(1));
        await recovered.Saved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await lifecycle.StopAsync(TestContext.Current.CancellationToken);
        await using var verify = database.CreateContext();
        var deliveryAfterRecovery = await verify.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmailDeliveryStatus.Pending, deliveryAfterRecovery.Status);
        Assert.Equal(Now.AddMinutes(4), deliveryAfterRecovery.NextAttemptAt);
        Assert.Equal(EmailAttemptOutcome.Abandoned, (await verify.Set<EmailDeliveryAttemptEntity>().SingleAsync(TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task TwoIndependentCoordinatorsShareDatabaseClaimsAndSendOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await new EfEmailDeliveryStore<MailContext>(setup).EnqueueAsync(Request(), TestContext.Current.CancellationToken);
        }

        var barrier = new SaveBarrierInterceptor();
        var clock = new FakeTimeProvider(Now);
        var transport = new RecordingTransport();
        await using var first = CreateProvider(database, clock, transport, scheduled: false, barrier);
        await using var second = CreateProvider(database, clock, transport, scheduled: false, barrier);
        var results = await Task.WhenAll(
            first.GetRequiredService<IEmailRetryDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken),
            second.GetRequiredService<IEmailRetryDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, results.Sum(result => result.Claimed));
        Assert.Equal(1, results.Sum(result => result.Succeeded));
        Assert.Equal(1, transport.Count);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<EmailDeliveryAttemptEntity>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(EmailDeliveryStatus.Succeeded, (await verify.Set<EmailDeliveryEntity>().SingleAsync(TestContext.Current.CancellationToken)).Status);
    }

    private static ServiceProvider CreateProvider(TestDatabase database, FakeTimeProvider clock, RecordingTransport transport,
        bool scheduled, params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext(interceptors));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IEmailTransport>(transport);
        services.AddDurableMail(options =>
        {
            options.Dispatch.AutoDispatchOnEnqueue = false;
            options.Retries.Scheduled.Enabled = scheduled;
            options.Retries.JitterRatio = 0;
        }).UseEntityFrameworkCore<MailContext>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private sealed class RecordingTransport : IEmailTransport
    {
        private int count;

        public int Count => Volatile.Read(ref count);
        public bool SawAmbientTransaction { get; private set; }
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            SawAmbientTransaction |= Transaction.Current is not null;
            Interlocked.Increment(ref count);
            Sent.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryObserverInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<EmailDeliveryAttemptEntity>().Any(x => x.Entity.Outcome == EmailAttemptOutcome.Abandoned))
            {
                Saved.TrySetResult();
            }

            return ValueTask.FromResult(result);
        }
    }
}
