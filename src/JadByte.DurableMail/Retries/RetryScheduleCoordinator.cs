using JadByte.DurableMail.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JadByte.DurableMail.Retries;

internal sealed class RetryScheduleCoordinator(TimeProvider timeProvider, IOptions<DurableMailOptions> options, ILogger<RetryScheduleCoordinator>? logger = null) : IDisposable
{
    private readonly ILogger<RetryScheduleCoordinator> _logger = logger ?? NullLogger<RetryScheduleCoordinator>.Instance;
    private readonly object _gate = new();
    private ITimer? _timer;
    private DateTimeOffset? _dueAt;
    private long _generation;
    private bool _disposed;

    public long GetGeneration()
    {
        lock (_gate)
        {
            return _generation;
        }
    }

    public void Reconcile(DateTimeOffset? next, Action signal, long expectedGeneration)
    {
        lock (_gate)
        {
            if (_disposed || _generation != expectedGeneration)
            {
                return;
            }

            _generation++;
            _timer?.Dispose();
            _timer = null;
            _dueAt = null;
            Consider(next, signal);
        }
    }

    public void Consider(DateTimeOffset? next, Action signal)
    {
        if (!options.Value.Retries.Scheduled.Enabled || next is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || (_dueAt.HasValue && _dueAt <= next))
            {
                return;
            }

            _timer?.Dispose();
            _dueAt = next;
            var generation = ++_generation;
            // Create disabled first: a zero-delay callback must not race assignment of _timer.
            _timer = timeProvider.CreateTimer(_ => Fire(generation, signal), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var delay = next.Value - timeProvider.GetUtcNow();
            // System timers have a finite maximum due time. A distant deadline is revisited once.
            delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, uint.MaxValue - 1d));
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
            MailLog.TimerArmed(_logger, next.Value);
        }
    }

    private void Fire(long generation, Action signal)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;
            _dueAt = null;
            _generation++;
        }

        signal();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _generation++;
            _timer?.Dispose();
            _timer = null;
            _dueAt = null;
        }
    }
}
