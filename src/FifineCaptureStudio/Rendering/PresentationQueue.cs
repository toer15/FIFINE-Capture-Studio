using System.Diagnostics;
using FifineCaptureStudio.Core.FrameGeneration;

namespace FifineCaptureStudio.Rendering;

/// <summary>
/// One item waiting for the native presenter. A frame is either a CPU BGRA frame (the compatibility
/// path used by the existing RIFE pipeline and self-test) or a GPU texture shared by the enhancement
/// engine. Both carry the original source sequence and arrival time through to presentation.
/// </summary>
public sealed record PresentationFrame(
    byte[]? Bgra,
    IntPtr SharedTextureHandle,
    int Width,
    int Height,
    long DueTicks,
    bool Generated,
    long SourceSequence,
    long SourceArrivalTicks,
    long EnqueuedTicks)
{
    public bool IsGpu => SharedTextureHandle != IntPtr.Zero;

    public double DueMs(long nowTicks) =>
        (DueTicks - nowTicks) * 1000.0 / Stopwatch.Frequency;

    public double QueueAgeMs(long nowTicks) =>
        (nowTicks - EnqueuedTicks) * 1000.0 / Stopwatch.Frequency;

    public static PresentationFrame FromCpu(ScheduledFrame frame) => new(
        frame.Bgra,
        IntPtr.Zero,
        frame.Width,
        frame.Height,
        frame.DueTicks,
        frame.Generated,
        frame.SourceSequence,
        0,
        Stopwatch.GetTimestamp());

    public static PresentationFrame FromGpu(GpuScheduledFrame frame) => new(
        null,
        frame.SharedTextureHandle,
        frame.Width,
        frame.Height,
        frame.DueTicks,
        frame.Generated,
        frame.SourceSequence,
        frame.SourceArrivalTicks,
        Stopwatch.GetTimestamp());
}

/// <summary>
/// Small bounded queue for presentation. When a producer gets ahead, the oldest frame is discarded
/// so the next frame shown is the freshest available rather than a stale backlog.
/// </summary>
public sealed class PresentationQueue
{
    private readonly object _gate = new();
    private readonly Queue<PresentationFrame> _frames = new();
    private readonly int _capacity;
    private readonly Action<PresentationFrame>? _onEvicted;
    private long _submitted;
    private long _dropped;

    public PresentationQueue(int capacity = 4, Action<PresentationFrame>? onEvicted = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _onEvicted = onEvicted;
    }

    public int Capacity => _capacity;
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    public long SubmittedFrames => Interlocked.Read(ref _submitted);
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>Returns false when an older queued frame had to be discarded to make room.</summary>
    public bool Enqueue(PresentationFrame frame)
    {
        var dropped = false;
        PresentationFrame? evicted = null;
        lock (_gate)
        {
            if (_frames.Count >= _capacity)
            {
                evicted = _frames.Dequeue();
                Interlocked.Increment(ref _dropped);
                dropped = true;
            }

            _frames.Enqueue(frame);
            Interlocked.Increment(ref _submitted);
        }

        if (evicted is not null)
        {
            try { _onEvicted?.Invoke(evicted); } catch { }
        }

        return !dropped;
    }

    public bool TryPeek(out PresentationFrame? frame)
    {
        lock (_gate)
        {
            if (_frames.Count == 0)
            {
                frame = null;
                return false;
            }

            frame = _frames.Peek();
            return true;
        }
    }

    public bool TryDequeue(out PresentationFrame? frame)
    {
        lock (_gate)
        {
            if (_frames.Count == 0)
            {
                frame = null;
                return false;
            }

            frame = _frames.Dequeue();
            return true;
        }
    }

    public void Clear()
    {
        var removed = new List<PresentationFrame>();
        lock (_gate)
        {
            removed.AddRange(_frames);
            _frames.Clear();
        }

        if (_onEvicted is not null)
        {
            foreach (var frame in removed)
            {
                try { _onEvicted(frame); } catch { }
            }
        }
    }
}
