namespace JadByte.DurableMail.Tests;

public sealed class InfrastructureFailureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScheduledTriggerRecoversAfterInfrastructureFailureBeforeClaim(bool failRecovery)
    {
        await using var harness = new RuntimeHarness(options => options.Retries.Scheduled.Enabled = true);
        var enqueued = await harness.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        var calls = 0;
        Task FailOnce(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("Database temporarily unavailable.");
            }

            return Task.CompletedTask;
        }

        if (failRecovery)
        {
            harness.Store.BeforeRecovery = FailOnce;
        }
        else
        {
            harness.Store.BeforeEligible = FailOnce;
        }

        await Assert.ThrowsAsync<IOException>(() => harness.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken));
        Assert.Empty(harness.Transport.Messages);
        Assert.Equal(0, harness.Store.Deliveries[enqueued.DeliveryId].Attempts);

        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Transport.Send = _ =>
        {
            sent.TrySetResult();
            return Task.CompletedTask;
        };
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await harness.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Single(harness.Transport.Messages);
        Assert.True(harness.Store.Deliveries[enqueued.DeliveryId].Terminal);

        var queries = harness.Store.EligibleQueries;
        harness.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(queries, harness.Store.EligibleQueries);
    }

    [Fact]
    public async Task FailedWakeUpQueryIsRetriedAndStopsAfterSuccessfulReconciliation()
    {
        await using var harness = new RuntimeHarness(options => options.Retries.Scheduled.Enabled = true);
        var calls = 0;
        var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Store.BeforeWakeUp = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("Database temporarily unavailable.");
            }

            reconciled.TrySetResult();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<IOException>(() => harness.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken));
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await harness.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);

        var completedQueries = calls;
        harness.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(completedQueries, calls);
        Assert.Empty(harness.Transport.Messages);
    }

    [Fact]
    public async Task DisabledScheduledTriggerRequiresAnotherManualDispatchAfterInfrastructureFailure()
    {
        await using var harness = new RuntimeHarness();
        var enqueued = await harness.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        var calls = 0;
        harness.Store.BeforeRecovery = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("Database temporarily unavailable.");
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<IOException>(() => harness.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken));
        harness.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, calls);
        Assert.Empty(harness.Transport.Messages);

        await harness.Dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Single(harness.Transport.Messages);
        Assert.True(harness.Store.Deliveries[enqueued.DeliveryId].Terminal);
    }
}
