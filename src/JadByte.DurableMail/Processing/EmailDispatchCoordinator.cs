using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JadByte.DurableMail.Processing;

internal sealed class EmailDispatchCoordinator(EmailDispatchRunner runner, ILogger<EmailDispatchCoordinator>? logger = null) : IDisposable
{
    private readonly ILogger<EmailDispatchCoordinator> _logger = logger ?? NullLogger<EmailDispatchCoordinator>.Instance;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _pendingIds = [];
    private readonly CancellationTokenSource _shutdown = new();
    private bool _pendingRetrySignal;
    private bool _stopping;
    private int _running;
    private int _consecutiveFailures;
    private bool _disposed;
    private Task<EmailDispatchResult> _activeTask = Task.FromResult(new EmailDispatchResult(0, 0, 0, 0));

    public void SignalPending(Guid deliveryId) => Signal(deliveryId, false);

    public void SignalRetries() => Signal(null, true);

    public Task<EmailDispatchResult> DispatchDueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("DurableMail is stopping.");
            }

            Signal(null, true);
            // A caller cancels its wait, not deliveries shared with other callers and automatic triggers.
            return _activeTask.WaitAsync(cancellationToken);
        }
    }

    private void Signal(Guid? deliveryId, bool retries)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            if (deliveryId.HasValue)
            {
                _pendingIds.Add(deliveryId.Value);
            }

            _pendingRetrySignal |= retries;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return;
            }

            // Do not retain HttpContext or an ambient transaction through ExecutionContext.
            if (ExecutionContext.IsFlowSuppressed())
            {
                _activeTask = RunTrackedAsync();
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    _activeTask = RunTrackedAsync();
                }
            }

            _ = _activeTask.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task<EmailDispatchResult> RunTrackedAsync()
    {
        var shutdownToken = _shutdown.Token;
        // Force an asynchronous boundary so enqueue never executes transport work inline.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        MailLog.Started(_logger);
        var total = new EmailDispatchResult(0, 0, 0, 0);
        Guid[] ids = [];
        var retries = false;
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    ids = [.. _pendingIds];
                    _pendingIds.Clear();
                    retries = _pendingRetrySignal;
                    _pendingRetrySignal = false;
                }

                total = EmailDispatchRunner.Add(total, await runner.RunAsync(ids, retries, SignalRetries, shutdownToken).ConfigureAwait(false));
                lock (_gate)
                {
                    _consecutiveFailures = 0;
                    if (_pendingIds.Count > 0 || _pendingRetrySignal)
                    {
                        continue;
                    }

                    Interlocked.Exchange(ref _running, 0);
                    return total;
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                // Reclaiming an already completed ID is harmless; dropping an unsent ID is not.
                _pendingIds.UnionWith(ids);
                _pendingRetrySignal |= retries;
                if (!_stopping)
                {
                    _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 1024);
                    runner.ScheduleRecovery(_consecutiveFailures, SignalRetries);
                }

                Interlocked.Exchange(ref _running, 0);
            }

            if (exception is not OperationCanceledException || !_shutdown.IsCancellationRequested)
            {
                MailLog.DispatchFailed(_logger, exception.GetType().FullName ?? exception.GetType().Name);
            }

            throw;
        }
        finally
        {
            MailLog.Stopped(_logger);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _stopping = true;
            active = _activeTask;
        }

        using var registration = cancellationToken.Register(_shutdown.Cancel);
        try
        {
            await active.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The tracked task has already logged its failure without sensitive exception content.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping = true;
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
