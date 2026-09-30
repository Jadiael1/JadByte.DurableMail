using System.Collections.Concurrent;
using JadByte.DurableMail.Processing;
using JadByte.DurableMail.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JadByte.DurableMail.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public async Task LoggingRegisteredAfterDurableMailIsRespected()
    {
        var services = new ServiceCollection();
        services.AddDurableMail(options =>
        {
            options.Dispatch.AutoDispatchOnEnqueue = false;
            options.Retries.Scheduled.Enabled = false;
        });
        using var logs = new CapturingLoggerProvider();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddSingleton<IEmailDeliveryStore, FakeStore>();
        services.AddSingleton<IEmailTransport, FakeEmailTransport>();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IEmailQueue>().EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        await provider.GetRequiredService<IEmailRetryDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Contains(logs.Events, entry => entry.EventId == 1);
        Assert.Contains(logs.Events, entry => entry.EventId == 6);
        Assert.DoesNotContain(logs.Events, entry => entry.Message.Contains("sensitive body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GracefulStopDrainsSignalsAlreadyAcceptedDuringTransport()
    {
        await using var runtime = new RuntimeHarness(options => options.Dispatch.AutoDispatchOnEnqueue = true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Transport.Send = async token => { started.TrySetResult(); await release.Task.WaitAsync(token); };
        await runtime.Queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runtime.Queue.EnqueueAsync(RuntimeHarness.Request("second"), TestContext.Current.CancellationToken);
        var stopping = runtime.Coordinator.StopAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, runtime.Transport.Messages.Count);
    }

    [Fact]
    public async Task ParallelDispatchUsesSeparateTransportScopesWithoutRequestExecutionContext()
    {
        var services = new ServiceCollection();
        var probe = new ScopeProbe();
        services.AddSingleton(probe);
        services.AddScoped<IEmailTransport, ScopedTransport>();
        services.AddSingleton<IEmailDeliveryStore, FakeStore>();
        services.AddDurableMail(options =>
        {
            options.Dispatch.AutoDispatchOnEnqueue = false;
            options.Dispatch.MaxDegreeOfParallelism = 2;
            options.Retries.Scheduled.Enabled = false;
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        probe.RequestValue.Value = "request-scoped-context";
        var queue = provider.GetRequiredService<IEmailQueue>();
        await queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(RuntimeHarness.Request(), TestContext.Current.CancellationToken);
        var result = await provider.GetRequiredService<IEmailRetryDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(2, probe.Instances.Distinct().Count());
        Assert.Equal(2, probe.Disposals);
        Assert.All(probe.ContextValues, Assert.Null);
    }

    [Fact]
    public void TimestampMathSaturatesInsteadOfOverflowing()
    {
        var now = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(DateTimeOffset.MaxValue, TimestampMath.AddDelay(now, TimeSpan.MaxValue));
        Assert.Equal(now.AddMinutes(2), TimestampMath.AddDelay(now, TimeSpan.FromMinutes(2)));
    }

    private sealed class ScopeProbe
    {
        public int Started;
        public int Disposals;
        public AsyncLocal<string?> RequestValue { get; } = new();
        public ConcurrentBag<string?> ContextValues { get; } = [];
        public ConcurrentBag<Guid> Instances { get; } = [];
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScopedTransport(ScopeProbe probe) : IEmailTransport, IDisposable
    {
        private readonly Guid _instance = Guid.NewGuid();

        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            probe.Instances.Add(_instance);
            probe.ContextValues.Add(probe.RequestValue.Value);
            if (Interlocked.Increment(ref probe.Started) == 2)
            {
                probe.BothStarted.TrySetResult();
            }

            await probe.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        public void Dispose() => Interlocked.Increment(ref probe.Disposals);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(int EventId, string Message)> Events { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Events);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<(int EventId, string Message)> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                events.Enqueue((eventId.Id, formatter(state, exception)));
        }
    }
}
