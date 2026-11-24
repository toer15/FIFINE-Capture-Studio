using System.Diagnostics;
using FifineCaptureStudio.Core.Enhancement;

namespace FifineCaptureStudio.Core.FrameGeneration;

/// <summary>
/// Turns a 60 fps source into an ordered 120 fps timeline: original, generated, original. Pairs are
/// processed on one worker thread with a newest-frame slot, so a slow inference drops stale frames
/// instead of building a backlog. Generated frames are only ever handed to the presenter; they are
/// never written to a recording.
/// </summary>
public sealed class FrameGenerationPipeline : IDisposable
{
    private const double SceneCutThreshold = 32.0;
    private const int MaxLateMs = 120;

    private sealed record InputFrame(byte[] Bgra, int Width, int Height, long Sequence, long ArrivalTicks);

    private readonly RifeOnnxEngine _engine;
    private readonly FrameGenerationService _service;
    private readonly Action<ScheduledFrame> _present;
    private readonly int _internalWidth;
    private readonly int _internalHeight;
    private readonly LatestFrameQueue<InputFrame> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    private byte[]? _previous;
    private long _previousSequence;
    private long _previousArrival;
    private bool _hasAnchor;
    private int _sourceWidth;
    private int _sourceHeight;
    private volatile bool _bypass;

    public FrameGenerationPipeline(
        RifeOnnxEngine engine,
        FrameGenerationService service,
        int internalWidth,
        int internalHeight,
        Action<ScheduledFrame> present)
    {
        _engine = engine;
        _service = service;
        _present = present;
        _internalWidth = internalWidth;
        _internalHeight = internalHeight;
    }

    public int InternalWidth => _internalWidth;
    public int InternalHeight => _internalHeight;

    /// <summary>
    /// Governance for a generated frame that is already late when inference finishes. Competitive
    /// drops it sooner, Smooth tolerates more lateness; either way originals keep flowing.
    /// </summary>
    public FrameGenerationLatency Latency { get; set; } = FrameGenerationLatency.Balanced;

    /// <summary>Immediately stops interpolation and presents source frames only.</summary>
    public bool Bypass
    {
        get => _bypass;
        set
        {
            _bypass = value;
            _service.SetStatus(value ? "Bypassed · original frames" : "Active · 2× interpolated");
        }
    }

    public void Start()
    {
        _worker ??= Task.Run(WorkerLoop);
    }

    /// <summary>Called from the capture pipeline. Copies the frame into the bounded slot.</summary>
    public void Submit(ReadOnlySpan<byte> bgra, int width, int height, long sequence, long arrivalTicks)
    {
        if (bgra.Length < width * height * 4)
        {
            return;
        }

        var copy = new byte[width * height * 4];
        bgra[..copy.Length].CopyTo(copy);
        if (!_queue.Submit(new InputFrame(copy, width, height, sequence, arrivalTicks)))
        {
            _service.NoteDropped();
        }
    }

    public void ResetHistory()
    {
        _previous = null;
        _hasAnchor = false;
    }

    private async Task WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            InputFrame? input;
            try
            {
                input = await _queue.TakeAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (input is null)
            {
                continue;
            }

            try
            {
                Process(input);
            }
            catch (Exception ex)
            {
                Log.Warn("Frame generation dropped a pair: " + ex.Message);
                ResetHistory();
            }
        }
    }

    private void Process(InputFrame input)
    {
        var scaleStart = Stopwatch.GetTimestamp();
        var scaled = new byte[_internalWidth * _internalHeight * 4];
        BgraScaler.Scale(input.Bgra, input.Width, input.Height, scaled, _internalWidth, _internalHeight);
        var scaleMs = Elapsed(scaleStart);

        _service.NoteSourceFrame();
        _service.NoteQueueAge(Elapsed(input.ArrivalTicks));

        if (_bypass)
        {
            _present(new ScheduledFrame
            {
                Bgra = scaled,
                Width = _internalWidth,
                Height = _internalHeight,
                DueTicks = Stopwatch.GetTimestamp(),
                Generated = false,
                SourceSequence = input.Sequence
            });
            _previous = null;
            _hasAnchor = false;
            return;
        }

        var resolutionChanged = _previous is not null
            && (_sourceWidth != input.Width || _sourceHeight != input.Height);
        _sourceWidth = input.Width;
        _sourceHeight = input.Height;

        if (_previous is null)
        {
            _previous = scaled;
            _previousSequence = input.Sequence;
            _previousArrival = input.ArrivalTicks;
            _hasAnchor = false;
            return;
        }

        if (resolutionChanged)
        {
            // Never warp across a resolution change; start a new temporal history.
            ResetHistory();
            _present(Frame(scaled, Stopwatch.GetTimestamp(), input.Sequence, generated: false));
            _previous = scaled;
            _previousSequence = input.Sequence;
            _previousArrival = input.ArrivalTicks;
            _hasAnchor = true;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var intervalTicks = Math.Clamp(
            input.ArrivalTicks - _previousArrival,
            Stopwatch.Frequency / 240,
            Stopwatch.Frequency / 24);

        // A reordered, dropped or resolution-changed frame invalidates temporal history.
        if (input.Sequence != _previousSequence + 1)
        {
            ResetHistory();
            _present(Frame(scaled, now, input.Sequence, generated: false));
            _previous = scaled;
            _previousSequence = input.Sequence;
            _previousArrival = input.ArrivalTicks;
            _hasAnchor = true;
            return;
        }

        var difference = BgraScaler.MeanDifference(_previous, scaled);
        if (difference > SceneCutThreshold)
        {
            // No meaningful motion field across a cut: present both originals and reset the timeline.
            _service.NoteSceneCut();
            if (!_hasAnchor)
            {
                _present(Frame(_previous, now, _previousSequence, generated: false));
            }

            _present(Frame(scaled, now, input.Sequence, generated: false));
            ResetHistory();
            _previous = scaled;
            _previousSequence = input.Sequence;
            _previousArrival = input.ArrivalTicks;
            _hasAnchor = true;
            return;
        }

        var generated = new byte[_internalWidth * _internalHeight * 4];
        var inferenceStart = Stopwatch.GetTimestamp();
        var ok = _engine.TryInterpolate(_previous, scaled, generated, out var run);
        var pipelineMs = Elapsed(inferenceStart) + scaleMs;
        // Schedule from the moment the pair is ready, not from before inference: anchoring earlier
        // would make every frame structurally late by the model's own run time.
        now = Stopwatch.GetTimestamp();
        if (!ok)
        {
            ResetHistory();
            _present(Frame(scaled, now, input.Sequence, generated: false));
            return;
        }

        _service.RecordInference(run.InferenceMs, pipelineMs);

        // The generated frame is due half a source interval after the pair became ready. If inference
        // itself overruns that, the frame is already late; show originals instead of a stale result.
        var intervalMs = intervalTicks * 1000.0 / Stopwatch.Frequency;
        var lateGeneratedMs = Math.Max(0, run.InferenceMs - intervalMs / 2);
        if (FrameGenerationSchedule.ShouldDrop(lateGeneratedMs, intervalMs, Latency))
        {
            _service.NoteSkipped();
            _service.SetStatus($"Behind ({run.InferenceMs:0.0} ms) · originals");
            if (!_hasAnchor)
            {
                _present(Frame(_previous, now, _previousSequence, generated: false));
            }

            _present(Frame(scaled, now, input.Sequence, generated: false));
            _previous = scaled;
            _previousSequence = input.Sequence;
            _previousArrival = input.ArrivalTicks;
            _hasAnchor = true;
            return;
        }

        _service.SetStatus("Active · 2× interpolated");
        var half = intervalTicks / 2;

        if (!_hasAnchor)
        {
            _present(Frame(_previous, now, _previousSequence, generated: false));
        }

        _present(new ScheduledFrame
        {
            Bgra = generated,
            Width = _internalWidth,
            Height = _internalHeight,
            DueTicks = now + half,
            Generated = true,
            SourceSequence = input.Sequence
        });

        _present(Frame(scaled, now + intervalTicks, input.Sequence, generated: false));

        _previous = scaled;
        _previousSequence = input.Sequence;
        _previousArrival = input.ArrivalTicks;
        _hasAnchor = true;
    }

    private ScheduledFrame Frame(byte[] pixels, long dueTicks, long sequence, bool generated) => new()
    {
        Bgra = pixels,
        Width = _internalWidth,
        Height = _internalHeight,
        DueTicks = dueTicks,
        Generated = generated,
        SourceSequence = sequence
    };

    private static double Elapsed(long startTicks) =>
        (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Complete();
        try { _worker?.Wait(500); } catch { }
        _cts.Dispose();
        _queue.Dispose();
    }
}
