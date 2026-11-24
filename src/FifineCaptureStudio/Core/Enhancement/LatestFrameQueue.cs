namespace FifineCaptureStudio.Core.Enhancement;

/// <summary>
/// A one-slot frame queue that always keeps the newest submission. When the processor falls behind,
/// the stale queued frame is replaced instead of a fresh frame being rejected, so latency stays
/// bounded and old gameplay can never appear seconds late. Sequence numbers still move forward on
/// every submission, so replacements are visible in the telemetry as drops.
/// </summary>
public sealed class LatestFrameQueue<T> : IDisposable where T : class
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);

    private T? _latest;
    private long _submitted;
    private long _replaced;
    private long _taken;

    /// <summary>Places a frame in the slot, replacing (and counting) any older one.</summary>
    public bool Submit(T frame)
    {
        bool replaced;
        lock (_gate)
        {
            replaced = _latest is not null;
            _latest = frame;
            _submitted++;
            if (replaced)
            {
                _replaced++;
            }
        }

        // Only signal when the slot went from empty to full; extra counts would just wake the
        // worker for nothing and make it spin after a burst.
        if (!replaced)
        {
            _signal.Release();
        }

        return !replaced;
    }

    public bool TryTake(out T? frame)
    {
        lock (_gate)
        {
            frame = _latest;
            _latest = null;
            if (frame is null)
            {
                return false;
            }

            _taken++;
            return true;
        }
    }

    /// <summary>Waits for a frame or for the queue to be completed/closed.</summary>
    public async Task<T?> TakeAsync(CancellationToken token)
    {
        await _signal.WaitAsync(token).ConfigureAwait(false);
        return TryTake(out var frame) ? frame : null;
    }

    public void Complete() => _signal.Release();

    public long Submitted => Interlocked.Read(ref _submitted);
    public long Replaced => Interlocked.Read(ref _replaced);
    public long Taken => Interlocked.Read(ref _taken);
    public bool HasFrame
    {
        get
        {
            lock (_gate)
            {
                return _latest is not null;
            }
        }
    }

    public void Dispose() => _signal.Dispose();
}
