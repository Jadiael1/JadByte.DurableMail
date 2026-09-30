using JadByte.DurableMail.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JadByte.DurableMail.Tests;

public sealed class DispatchTests
{
    [Fact]
    public async Task ManualDispatchSucceedsAndStopsWhenEmpty()
    {
        await using var runtime = new RuntimeHarness();
        var queued = await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        var result = await runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new EmailDispatchResult(1, 1, 0, 0), result);
        Assert.True(runtime.Store.Deliveries[queued.DeliveryId].Terminal);
        Assert.Equal(new EmailDispatchResult(0, 0, 0, 0), await runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken));
        Assert.Single(runtime.Transport.Messages);
    }

    [Fact]
    public async Task TransientFailuresBackoffUntilTotalAttemptLimit()
    {
        await using var runtime = new RuntimeHarness(options => options.Retries.MaxAttempts = 3);
        runtime.Transport.Send = _ => throw new IOException("secret-token");
        var queued = await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        var first = await runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, first.TransientFailures);
        var delivery = runtime.Store.Deliveries[queued.DeliveryId];
        Assert.Equal(runtime.Time.GetUtcNow() + TimeSpan.FromMinutes(2), delivery.Next);
        Assert.Equal(0, (await runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken)).Claimed);
        runtime.Time.Advance(TimeSpan.FromMinutes(2));
        await runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(runtime.Time.GetUtcNow() + TimeSpan.FromMinutes(4), delivery.Next);
        runtime.Time.Advance(TimeSpan.FromMinutes(4));
        await runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.True(delivery.Terminal);
        Assert.Equal(3, delivery.Attempts);
        Assert.DoesNotContain("secret-token", string.Join(" ", runtime.Store.Failures.Select(failure => failure.ErrorMessage)));
    }

    [Fact]
    public async Task PermanentFailureIsTerminalImmediately()
    {
        await using var runtime = new RuntimeHarness(classifier: new PermanentClassifier());
        runtime.Transport.Send = _ => throw new FormatException();
        var queued = await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(1, (await runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken)).PermanentFailures);
        runtime.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken)).Claimed);
        Assert.Equal(1, runtime.Store.Deliveries[queued.DeliveryId].Attempts);
    }

    [Fact]
    public async Task EnqueueReturnsBeforeBlockedTransportAndSignalsOnlyAfterCommit()
    {
        await using var runtime = new RuntimeHarness(options => options.Dispatch.AutoDispatchOnEnqueue = true);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Store.BeforeCommit = token => commit.Task.WaitAsync(token);
        runtime.Transport.Send = async token => { started.TrySetResult(); await release.Task.WaitAsync(token); };
        var enqueue = runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        Assert.Empty(runtime.Transport.Messages);
        Assert.False(enqueue.IsCompleted);
        commit.SetResult();
        await enqueue.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(runtime.Store.Committed.Task.IsCompletedSuccessfully);
        release.SetResult();
    }

    [Fact]
    public async Task ConcurrentSignalsShareOneRunnerAndNewSignalIsNotLost()
    {
        await using var runtime = new RuntimeHarness(options => options.Dispatch.AutoDispatchOnEnqueue = true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Transport.Send = async token => { started.TrySetResult(); await release.Task.WaitAsync(token); };
        await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var waiters = Enumerable.Range(0, 20).Select(_ => runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken)).ToArray();
        await runtime.Queue.EnqueueAsync(RuntimeHarness.Request("second"), TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, runtime.Transport.Messages.Count);
        Assert.Equal(1, runtime.Transport.MaxActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnqueueRetryTriggerControlsEligibleScan(bool enabled)
    {
        await using var runtime = new RuntimeHarness(options =>
        {
            options.Dispatch.AutoDispatchOnEnqueue = true;
            options.Retries.Enqueue.Enabled = enabled;
        });
        await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        await runtime.Coordinator.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(enabled, runtime.Store.EligibleQueries > 0);
        Assert.Single(runtime.Transport.Messages);
    }

    [Fact]
    public async Task CancelledEnqueueNeverSignals()
    {
        await using var runtime = new RuntimeHarness(options => options.Dispatch.AutoDispatchOnEnqueue = true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), cancellation.Token));
        Assert.Empty(runtime.Transport.Messages);
    }

    [Fact]
    public async Task ManualCancellationCancelsWaitWithoutCancellingSharedDelivery()
    {
        await using var runtime = new RuntimeHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Transport.Send = async token => { started.TrySetResult(); await release.Task.WaitAsync(token); };
        await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var dispatch = runtime.Dispatcher.DispatchDueAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        release.SetResult();
        await runtime.Coordinator.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(runtime.Store.Deliveries).Value.Terminal);
    }

    [Fact]
    public async Task ShutdownCancellationLeavesInflightClaimForRecovery()
    {
        await using var runtime = new RuntimeHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Transport.Send = async token => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        var dispatch = runtime.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Coordinator.StopAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        var delivery = Assert.Single(runtime.Store.Deliveries).Value;
        Assert.True(delivery.Processing);
        Assert.False(delivery.Terminal);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    public async Task StartupTriggerAndScheduledRehydrationAreIndependent(bool scheduled, bool startup, int sent)
    {
        await using var runtime = new RuntimeHarness(options =>
        {
            options.Retries.Scheduled.Enabled = scheduled;
            options.Retries.Startup.Enabled = startup;
        });
        await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        var lifecycle = Assert.Single(runtime.Services.GetServices<IHostedService>());
        await lifecycle.StartAsync(TestContext.Current.CancellationToken);
        await lifecycle.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sent, runtime.Transport.Messages.Count);
    }

    private sealed class PermanentClassifier : IEmailFailureClassifier
    {
        public EmailFailureKind Classify(Exception exception) => EmailFailureKind.Permanent;
    }
}
