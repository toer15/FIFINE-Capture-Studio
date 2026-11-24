using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace FifineCaptureStudio.Core.Enhancement;

/// <summary>
/// Arrival metadata carried with a preview frame from decode to the enhancement worker.
/// <see cref="ArrivalTicks"/> is the moment the decoded frame became available to the app, which is
/// the earliest boundary this process can observe; camera exposure and network time are outside it.
/// </summary>
public readonly record struct FrameArrival(long Sequence, long ArrivalTicks, double DecodeMs);

/// <summary>
/// Owns the GPU engine and the live statistics shown in the UI. Processing happens on a worker
/// thread; the UI never touches the Direct3D context directly.
/// </summary>
public sealed class EnhancementService : IDisposable
{
    private const int LatencySampleCapacity = 240;

    private readonly SettingsService _settings;
    private readonly object _engineGate = new();
    private readonly object _statsGate = new();

    private EnhancementEngine? _engine;
    private GpuDetection.AdapterEntry? _primary;
    private string _status = "Detecting GPU…";
    private int _framesSinceStats = 30;
    private DateTime _lastVramQuery = DateTime.MinValue;

    private double _fps;
    private double _avgMs;
    private double _uploadMs;
    private double _denoiseMs;
    private double _gradeMs;
    private double _upscaleMs;
    private double _readbackMs;
    private double _readbackWaitMs;
    private double _queueAgeMs;
    private double _decodeMs;
    private double _vramMb;
    private long _processed;
    private long _dropped;
    private long _presented;
    private long _fpsFrames;
    private DateTime _fpsWindow = DateTime.UtcNow;
    private long _presentFrames;
    private double _presentFps;
    private DateTime _presentWindow = DateTime.UtcNow;
    private int _lastInputWidth;
    private int _lastInputHeight;
    private int _lastOutputWidth;
    private int _lastOutputHeight;
    private readonly List<double> _presentSamples = new(LatencySampleCapacity);
    private readonly List<double> _totalSamples = new(LatencySampleCapacity);
    private readonly List<double> _targetSamples = new(LatencySampleCapacity);
    private SuperResolutionTarget _sampleTarget = SuperResolutionTarget.Native;
    private volatile bool _use4KFallback;

    public EnhancementService(SettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<GpuDetection.AdapterEntry> Adapters { get; private set; } = Array.Empty<GpuDetection.AdapterEntry>();
    public GpuAdapterInfo? Adapter => _primary?.Info;
    public bool Available => _primary is not null;
    public string Status => _status;
    public string AdapterName => _primary?.Info.Name ?? "No GPU detected";

    public long ProcessedFrames => Interlocked.Read(ref _processed);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long PresentedFrames => Interlocked.Read(ref _presented);
    public double MeasuredFps => _fps;
    public double PresentedFps => _presentFps;
    public double AverageFrameMs => _avgMs;
    public double UploadMs => _uploadMs;
    public double DenoiseMs => _denoiseMs;
    public double ColourMs => _gradeMs;
    public double UpscaleMs => _upscaleMs;
    public double ReadbackMs => _readbackMs;
    public double ReadbackWaitMs => _readbackWaitMs;
    public double QueueAgeMs => _queueAgeMs;
    public double DecodeMs => _decodeMs;
    public double VramUsedMb => _vramMb;
    public int LastInputWidth => _lastInputWidth;
    public int LastInputHeight => _lastInputHeight;
    public int LastOutputWidth => _lastOutputWidth;
    public int LastOutputHeight => _lastOutputHeight;

    public EnhancementConfig Config => _settings.Current.Enhancement.ToConfig();

    /// <summary>
    /// True when the requested 4K target exceeded the 60 FPS total-duration budget in measured
    /// frames. The persisted choice stays 4K; only the runtime effective target falls back to 1440p
    /// so the user can see what happened and choose a different preset deliberately.
    /// </summary>
    public bool TargetFallbackActive => _use4KFallback;

    public string TargetFallbackNote => _use4KFallback
        ? "4K P95 exceeded 16.67 ms · runtime output is 2560 × 1440 until the target changes."
        : string.Empty;

    public static bool Needs4KFallback(SuperResolutionTarget target, double p95Ms, int sampleCount) =>
        target == SuperResolutionTarget.P2160 && sampleCount >= 60 && p95Ms > 16.67;

    public bool ShouldEnhancePreview => Available
                                        && _settings.Current.Enhancement.Enabled
                                        && _settings.Current.Enhancement.PreviewEnhanced;

    public bool ShouldEnhanceFullscreen => Available
                                           && _settings.Current.Enhancement.Enabled
                                           && _settings.Current.Enhancement.FullscreenEnhanced;

    /// <summary>Average of decode-completion to compositor-present, in milliseconds.</summary>
    public double PresentAverageMs
    {
        get { lock (_statsGate) { return AverageCore(_presentSamples); } }
    }

    public double PresentP50Ms
    {
        get { lock (_statsGate) { return PercentileCore(_presentSamples, 0.50); } }
    }

    public double PresentP95Ms
    {
        get { lock (_statsGate) { return PercentileCore(_presentSamples, 0.95); } }
    }
    public int PresentSampleCount
    {
        get
        {
            lock (_statsGate)
            {
                return _presentSamples.Count;
            }
        }
    }

    public double TotalAverageMs
    {
        get { lock (_statsGate) { return AverageCore(_totalSamples); } }
    }

    public double TotalP95Ms
    {
        get { lock (_statsGate) { return PercentileCore(_totalSamples, 0.95); } }
    }

    public void Initialize()
    {
        try
        {
            Adapters = GpuDetection.Detect();
            _primary = GpuDetection.Primary(Adapters);
            if (_primary is null)
            {
                _status = "No Direct3D 11 adapter found — enhancement disabled.";
                return;
            }

            _status = $"{_primary.Info.Name} · Direct3D 11 · {_primary.Info.VramText}";
            Log.Info("Enhancement GPU: " + _status);
        }
        catch (Exception ex)
        {
            _status = "GPU detection failed: " + ex.Message;
            Log.Error("Enhancement initialisation failed", ex);
        }
    }

    /// <summary>Processes one frame on the calling (worker) thread.</summary>
    public EnhancedFrame? Process(BitmapSource frame, FrameArrival arrival)
    {
        if (!ShouldEnhancePreview)
        {
            return null;
        }

        var engine = EnsureEngine();
        if (engine is null)
        {
            return null;
        }

        var started = Stopwatch.GetTimestamp();
        var queueWaitMs = (started - arrival.ArrivalTicks) * 1000.0 / Stopwatch.Frequency;
        try
        {
            var result = engine.Process(frame, EffectiveConfig(Config));
            if (result is null)
            {
                return null;
            }

            var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            RecordFrame(result, ms, queueWaitMs, arrival.DecodeMs, engine);
            return result;
        }
        catch (Exception ex)
        {
            _status = "Enhancement failed: " + ex.Message;
            Log.Error("Enhancement frame failed", ex);
            return null;
        }
    }

    /// <summary>Convenience overload for callers that only have a bitmap (tests, tooling).</summary>
    public EnhancedFrame? Process(BitmapSource frame) =>
        Process(frame, new FrameArrival(0, Stopwatch.GetTimestamp(), 0));

    /// <summary>
    /// Processes a fullscreen frame and leaves the result in GPU memory for the native presenter.
    /// The WPF preview keeps its own CPU path; this path is deliberately readback-free.
    /// </summary>
    public EnhancedGpuFrame? ProcessShared(BitmapSource frame, FrameArrival arrival, long dueTicks)
    {
        if (!ShouldEnhanceFullscreen)
        {
            return null;
        }

        var engine = EnsureEngine();
        if (engine is null)
        {
            return null;
        }

        var started = Stopwatch.GetTimestamp();
        var queueWaitMs = (started - arrival.ArrivalTicks) * 1000.0 / Stopwatch.Frequency;
        try
        {
            var result = engine.ProcessShared(frame, EffectiveConfig(Config), arrival.Sequence, arrival.ArrivalTicks, dueTicks);
            if (result is null)
            {
                return null;
            }

            var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            RecordSharedFrame(result, ms, queueWaitMs, arrival.DecodeMs, engine);
            return result;
        }
        catch (Exception ex)
        {
            _status = "Enhancement failed: " + ex.Message;
            Log.Error("Shared enhancement frame failed", ex);
            return null;
        }
    }

    public void NoteDropped() => Interlocked.Increment(ref _dropped);

    /// <summary>
    /// Records that a processed frame reached the compositor. This is an application-level
    /// capture-to-preview measurement, not glass-to-glass latency.
    /// </summary>
    public void NotePresented(long sequence, long arrivalTicks, long presentTicks)
    {
        if (arrivalTicks <= 0 || presentTicks <= 0)
        {
            return;
        }

        var ms = (presentTicks - arrivalTicks) * 1000.0 / Stopwatch.Frequency;
        if (ms < 0 || ms > 5000)
        {
            return;
        }

        lock (_statsGate)
        {
            Interlocked.Increment(ref _presented);
            AddSample(_presentSamples, ms);
            _presentFrames++;
            var now = DateTime.UtcNow;
            var elapsed = (now - _presentWindow).TotalSeconds;
            if (elapsed >= 0.5)
            {
                _presentFps = Math.Round(_presentFrames / elapsed, 1);
                _presentFrames = 0;
                _presentWindow = now;
            }
        }
    }

    private EnhancementEngine? EnsureEngine()
    {
        if (_primary is null)
        {
            return null;
        }

        lock (_engineGate)
        {
            if (_engine is not null)
            {
                return _engine;
            }

            try
            {
                _engine = new EnhancementEngine(_primary);
                _status = $"{_engine.AdapterName} · Direct3D 11 compute";
                return _engine;
            }
            catch (Exception ex)
            {
                _status = "GPU engine unavailable: " + ex.Message;
                Log.Error("Could not create enhancement engine", ex);
                return null;
            }
        }
    }

    private EnhancementConfig EffectiveConfig(EnhancementConfig config) =>
        _use4KFallback && config.Target == SuperResolutionTarget.P2160
            ? config with { Target = SuperResolutionTarget.P1440 }
            : config;

    private void RecordTargetSample(double totalMs)
    {
        var requested = Config.Target;
        if (requested != _sampleTarget)
        {
            _targetSamples.Clear();
            _sampleTarget = requested;
            _use4KFallback = false;
        }

        AddSample(_targetSamples, totalMs);
        if (!_use4KFallback && Needs4KFallback(requested, PercentileCore(_targetSamples, 0.95), _targetSamples.Count))
        {
            _use4KFallback = true;
        }
    }

    private void RecordSharedFrame(EnhancedGpuFrame result, double totalMs, double queueWaitMs, double decodeMs, EnhancementEngine engine)
    {
        lock (_statsGate)
        {
            Interlocked.Increment(ref _processed);
            _fpsFrames++;
            var now = DateTime.UtcNow;
            var elapsed = (now - _fpsWindow).TotalSeconds;
            if (elapsed >= 0.5)
            {
                _fps = Math.Round(_fpsFrames / elapsed, 1);
                _fpsFrames = 0;
                _fpsWindow = now;
            }

            _lastInputWidth = result.SourceWidth;
            _lastInputHeight = result.SourceHeight;
            _lastOutputWidth = result.OutputWidth;
            _lastOutputHeight = result.OutputHeight;
            _avgMs = Ewma(_avgMs, totalMs);
            _uploadMs = Ewma(_uploadMs, result.Timings.UploadMs);
            _queueAgeMs = Ewma(_queueAgeMs, Math.Max(0, queueWaitMs));
            _decodeMs = Ewma(_decodeMs, Math.Max(0, decodeMs));
            AddSample(_totalSamples, totalMs);
            RecordTargetSample(totalMs);

            if (++_framesSinceStats >= 30 || (now - _lastVramQuery).TotalSeconds > 2)
            {
                _framesSinceStats = 0;
                _lastVramQuery = now;
                _vramMb = engine.QueryVramUsedMb();
            }
        }
    }

    private void RecordFrame(EnhancedFrame result, double totalMs, double queueWaitMs, double decodeMs, EnhancementEngine engine)
    {
        var timings = result.Timings;
        lock (_statsGate)
        {
            Interlocked.Increment(ref _processed);
            _fpsFrames++;
            var now = DateTime.UtcNow;
            var elapsed = (now - _fpsWindow).TotalSeconds;
            if (elapsed >= 0.5)
            {
                _fps = Math.Round(_fpsFrames / elapsed, 1);
                _fpsFrames = 0;
                _fpsWindow = now;
            }

            _lastInputWidth = result.SourceWidth;
            _lastInputHeight = result.SourceHeight;
            _lastOutputWidth = result.OutputWidth;
            _lastOutputHeight = result.OutputHeight;

            _avgMs = Ewma(_avgMs, totalMs);
            _uploadMs = Ewma(_uploadMs, timings.UploadMs);
            RecordTargetSample(totalMs);
            _denoiseMs = Ewma(_denoiseMs, timings.DenoiseMs);
            _gradeMs = Ewma(_gradeMs, timings.ColourMs);
            _upscaleMs = Ewma(_upscaleMs, timings.UpscaleMs);
            _readbackMs = Ewma(_readbackMs, timings.ReadbackMs);
            _readbackWaitMs = Ewma(_readbackWaitMs, timings.ReadbackWaitMs);
            _queueAgeMs = Ewma(_queueAgeMs, Math.Max(0, queueWaitMs));
            _decodeMs = Ewma(_decodeMs, Math.Max(0, decodeMs));
            AddSample(_totalSamples, totalMs);

            if (++_framesSinceStats >= 30 || (now - _lastVramQuery).TotalSeconds > 2)
            {
                _framesSinceStats = 0;
                _lastVramQuery = now;
                _vramMb = engine.QueryVramUsedMb();
            }
        }
    }

    private static void AddSample(List<double> samples, double value)
    {
        samples.Add(value);
        if (samples.Count > LatencySampleCapacity)
        {
            samples.RemoveRange(0, samples.Count - LatencySampleCapacity);
        }
    }

    private static double Ewma(double current, double sample) =>
        current <= 0 ? sample : (current * 0.9) + (sample * 0.1);

    private static double AverageCore(List<double> samples) =>
        samples.Count == 0 ? 0 : samples.Average();

    private static double PercentileCore(List<double> samples, double fraction)
    {
        if (samples.Count < 8)
        {
            return 0;
        }

        var copy = samples.ToArray();
        Array.Sort(copy);
        var index = (int)Math.Round((copy.Length - 1) * fraction);
        return copy[Math.Clamp(index, 0, copy.Length - 1)];
    }

    public void Reload()
    {
        lock (_statsGate)
        {
            var target = Config.Target;
            if (target != _sampleTarget)
            {
                _targetSamples.Clear();
                _sampleTarget = target;
                _use4KFallback = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_engineGate)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
