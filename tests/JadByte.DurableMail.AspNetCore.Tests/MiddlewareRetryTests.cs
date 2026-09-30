using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JadByte.DurableMail.AspNetCore.Tests;

public sealed class MiddlewareRetryTests
{
    [Fact]
    public void IntervalDefaultsToOneMinuteAndTriggerIsDisabled()
    {
        var options = new DurableMailOptions();
        Assert.False(options.Retries.Middleware.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Retries.Middleware.Interval);
    }

    [Fact]
    public async Task DisabledOnlyCallsNext()
    {
        var signaled = 0;
        var next = 0;
        var middleware = new DurableMailRetryMiddleware(
            _ => { next++; return Task.CompletedTask; },
            new MiddlewareRetryGate(new FakeTimeProvider(), TimeSpan.FromMinutes(1)),
            false,
            () => signaled++);
        await middleware.InvokeAsync(new DefaultHttpContext());
        Assert.Equal(1, next);
        Assert.Equal(0, signaled);
    }

    [Fact]
    public async Task EnabledSignalsFirstRequestAndThenOnlyAfterInterval()
    {
        var clock = new FakeTimeProvider();
        var signaled = 0;
        var middleware = new DurableMailRetryMiddleware(
            _ => Task.CompletedTask,
            new MiddlewareRetryGate(clock, TimeSpan.FromMinutes(1)),
            true,
            () => signaled++);

        await middleware.InvokeAsync(new DefaultHttpContext());
        Assert.Equal(1, signaled);
        clock.Advance(TimeSpan.FromSeconds(59));
        await middleware.InvokeAsync(new DefaultHttpContext());
        Assert.Equal(1, signaled);
        clock.Advance(TimeSpan.FromSeconds(1));
        await middleware.InvokeAsync(new DefaultHttpContext());
        Assert.Equal(2, signaled);
    }

    [Fact]
    public async Task ConcurrentRequestsProduceOneSignalPerInterval()
    {
        var clock = new FakeTimeProvider();
        var signaled = 0;
        var next = 0;
        var middleware = new DurableMailRetryMiddleware(
            _ => { Interlocked.Increment(ref next); return Task.CompletedTask; },
            new MiddlewareRetryGate(clock, TimeSpan.FromMinutes(1)),
            true,
            () => Interlocked.Increment(ref signaled));
        await Parallel.ForEachAsync(Enumerable.Range(0, 500), TestContext.Current.CancellationToken,
            async (_, _) => await middleware.InvokeAsync(new DefaultHttpContext()));
        Assert.Equal(1, signaled);
        Assert.Equal(500, next);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Parallel.ForEachAsync(Enumerable.Range(0, 500), TestContext.Current.CancellationToken,
            async (_, _) => await middleware.InvokeAsync(new DefaultHttpContext()));
        Assert.Equal(2, signaled);
        Assert.Equal(1000, next);
    }

    [Fact]
    public void GateTreatsZeroAsAValidInitialTimestamp()
    {
        var clock = new IndependentClock();
        var gate = new MiddlewareRetryGate(clock, TimeSpan.FromMinutes(1));
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        clock.Timestamp = 59;
        Assert.False(gate.TryEnter());
        clock.Timestamp = 60;
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void GateIgnoresWallClockChanges()
    {
        var clock = new IndependentClock();
        var gate = new MiddlewareRetryGate(clock, TimeSpan.FromMinutes(1));
        Assert.True(gate.TryEnter());
        clock.UtcNow = clock.UtcNow.AddDays(30);
        Assert.False(gate.TryEnter());
        clock.UtcNow = clock.UtcNow.AddDays(-60);
        clock.Timestamp = 60;
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void MiddlewarePassesThroughNextTask()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new DurableMailRetryMiddleware(
            _ => completion.Task,
            new MiddlewareRetryGate(new FakeTimeProvider(), TimeSpan.FromMinutes(1)),
            true,
            () => { });
        Assert.Same(completion.Task, middleware.InvokeAsync(new DefaultHttpContext()));
        completion.SetResult();
    }

    private sealed class IndependentClock : TimeProvider
    {
        internal long Timestamp { get; set; }
        internal DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
