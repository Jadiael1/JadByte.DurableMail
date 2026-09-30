using System.Collections.Concurrent;
using JadByte.DurableMail.Processing;
using JadByte.DurableMail.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace JadByte.DurableMail.Tests;

internal sealed class FakeEmailTransport : IEmailTransport
{
    private int _active;
    public int MaxActive;
    public ConcurrentQueue<EmailMessage> Messages { get; } = new();
    public Func<CancellationToken, Task> Send { get; set; } = _ => Task.CompletedTask;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var active = Interlocked.Increment(ref _active);
        Interlocked.Exchange(ref MaxActive, Math.Max(MaxActive, active));
        Messages.Enqueue(message);
        try
        {
            await Send(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

internal sealed class FakeStore : IEmailDeliveryStore
{
    internal sealed class Delivery(EmailEnqueueStoreRequest request)
    {
        public EmailEnqueueStoreRequest Request { get; } = request;
        public int Attempts { get; set; }
        public DateTimeOffset? Next { get; set; }
        public bool Processing { get; set; }
        public bool Terminal { get; set; }
    }

    private readonly object _gate = new();
    public ConcurrentDictionary<Guid, Delivery> Deliveries { get; } = new();
    public Func<CancellationToken, Task>? BeforeCommit { get; set; }
    public Func<CancellationToken, Task>? BeforeRecovery { get; set; }
    public Func<CancellationToken, Task>? BeforeEligible { get; set; }
    public Func<CancellationToken, Task>? BeforeWakeUp { get; set; }
    public ConcurrentQueue<EmailFailureDetails> Failures { get; } = new();
    public int EligibleQueries;
    public int RecoveryQueries;
    public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<EmailEnqueueResult> EnqueueAsync(EmailEnqueueStoreRequest request, CancellationToken cancellationToken = default)
    {
        if (BeforeCommit is not null)
        {
            await BeforeCommit(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        Deliveries[id] = new(request);
        Committed.TrySetResult();
        return new(id, EmailEnqueueOutcome.Created);
    }

    public async Task<IReadOnlyList<Guid>> GetEligibleDeliveryIdsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref EligibleQueries);
        if (BeforeEligible is not null)
        {
            await BeforeEligible(cancellationToken);
        }

        lock (_gate)
        {
            return Deliveries.Where(pair => !pair.Value.Terminal && !pair.Value.Processing && (pair.Value.Next is null || pair.Value.Next <= now)).Take(take).Select(pair => pair.Key).ToArray();
        }
    }

    public Task<ClaimedEmailDelivery?> TryClaimAsync(Guid deliveryId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var delivery = Deliveries[deliveryId];
            if (delivery.Terminal || delivery.Processing || delivery.Next > now)
            {
                return Task.FromResult<ClaimedEmailDelivery?>(null);
            }

            delivery.Processing = true;
            return Task.FromResult<ClaimedEmailDelivery?>(new(deliveryId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ++delivery.Attempts, delivery.Request.MaxAttempts, delivery.Request.Request.Message));
        }
    }

    public Task<bool> MarkSucceededAsync(ClaimedEmailDelivery claim, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Deliveries[claim.DeliveryId].Processing = false;
            Deliveries[claim.DeliveryId].Terminal = true;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkTransientFailureAsync(ClaimedEmailDelivery claim, DateTimeOffset now, DateTimeOffset nextAttemptAt, EmailFailureDetails failure, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var delivery = Deliveries[claim.DeliveryId];
            delivery.Processing = false;
            delivery.Terminal = delivery.Attempts >= claim.MaxAttempts;
            delivery.Next = nextAttemptAt;
            Failures.Enqueue(failure);
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkPermanentFailureAsync(ClaimedEmailDelivery claim, DateTimeOffset now, EmailFailureDetails failure, CancellationToken cancellationToken = default)
    {
        Failures.Enqueue(failure);
        return MarkSucceededAsync(claim, now, cancellationToken);
    }

    public async Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, Func<int, TimeSpan> retryDelay, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref RecoveryQueries);
        if (BeforeRecovery is not null)
        {
            await BeforeRecovery(cancellationToken);
        }

        return 0;
    }

    public async Task<DateTimeOffset?> GetNextWakeUpAtAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (BeforeWakeUp is not null)
        {
            await BeforeWakeUp(cancellationToken);
        }

        lock (_gate)
        {
            return Deliveries.Values.Where(delivery => !delivery.Terminal && !delivery.Processing)
                .Select(delivery => (DateTimeOffset?)(delivery.Next ?? now)).Min();
        }
    }

    public Task<EmailMaintenanceResult> PruneAsync(EmailPruneRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new EmailMaintenanceResult(0, 0));
}

internal sealed class RuntimeHarness : IAsyncDisposable
{
    public FakeStore Store { get; } = new();
    public FakeEmailTransport Transport { get; } = new();
    public FakeTimeProvider Time { get; } = new();
    public ServiceProvider Services { get; }
    public IEmailQueue Queue => Services.GetRequiredService<IEmailQueue>();
    public IEmailRetryDispatcher Dispatcher => Services.GetRequiredService<IEmailRetryDispatcher>();
    public EmailDispatchCoordinator Coordinator => Services.GetRequiredService<EmailDispatchCoordinator>();

    public RuntimeHarness(Action<DurableMailOptions>? configure = null, IEmailFailureClassifier? classifier = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddScoped<IEmailDeliveryStore>(_ => Store);
        services.AddSingleton<IEmailTransport>(Transport);
        if (classifier is not null)
        {
            services.AddSingleton(classifier);
        }

        services.AddDurableMail(options =>
        {
            options.Dispatch.AutoDispatchOnEnqueue = false;
            options.Retries.Scheduled.Enabled = false;
            options.Retries.JitterRatio = 0;
            configure?.Invoke(options);
        });
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Coordinator.StopAsync(timeout.Token);
        await Services.DisposeAsync();
    }

    public static EmailEnqueueRequest Request(string body = "sensitive body") => new(new("recipient@example.test", "Subject", body));
}
