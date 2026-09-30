namespace JadByte.DurableMail.AspNetCore;

internal sealed class MiddlewareRetryGate(TimeProvider timeProvider, TimeSpan interval)
{
    private long _lastSignalTimestamp;
    private int _checking;
    private bool _hasSignaled;

    internal bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            var now = timeProvider.GetTimestamp();
            if (_hasSignaled && timeProvider.GetElapsedTime(Interlocked.Read(ref _lastSignalTimestamp), now) < interval)
            {
                return false;
            }

            Interlocked.Exchange(ref _lastSignalTimestamp, now);
            _hasSignaled = true;
            return true;
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }
}
