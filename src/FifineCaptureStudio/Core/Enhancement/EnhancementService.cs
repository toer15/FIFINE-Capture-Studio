using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace FifineCaptureStudio.Core.Enhancement;

/// <summary>
/// Owns the GPU engine and the live statistics shown in the UI. Processing happens on a worker
/// thread; the UI never touches the Direct3D context directly.
/// </summary>
public sealed class EnhancementService : IDisposable
{
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
    private double _vramMb;
    private long _processed;
    private long _dropped;
    private long _fpsFrames;
    private DateTime _fpsWindow = DateTime.UtcNow;

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
    public double MeasuredFps => _fps;
    public double AverageFrameMs => _avgMs;
    public double UploadMs => _uploadMs;
    public double DenoiseMs => _denoiseMs;
    public double ColourMs => _gradeMs;
    public double UpscaleMs => _upscaleMs;
    public double ReadbackMs => _readbackMs;
    public double VramUsedMb => _vramMb;

    public EnhancementConfig Config => _settings.Current.Enhancement.ToConfig();

    public bool ShouldEnhancePreview => Available
                                        && _settings.Current.Enhancement.Enabled
                                        && _settings.Current.Enhancement.PreviewEnhanced;

    public bool ShouldEnhanceFullscreen => Available
                                           && _settings.Current.Enhancement.Enabled
                                           && _settings.Current.Enhancement.FullscreenEnhanced;

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
    public EnhancedFrame? Process(BitmapSource frame)
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
        try
        {
            var result = engine.Process(frame, Config);
            if (result is null)
            {
                return null;
            }

            var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            RecordFrame(result.Timings, ms, engine);
            return result;
        }
        catch (Exception ex)
        {
            _status = "Enhancement failed: " + ex.Message;
            Log.Error("Enhancement frame failed", ex);
            return null;
        }
    }

    public void NoteDropped() => Interlocked.Increment(ref _dropped);

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

    private void RecordFrame(EnhancementTimings timings, double totalMs, EnhancementEngine engine)
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

            _avgMs = Ewma(_avgMs, totalMs);
            _uploadMs = Ewma(_uploadMs, timings.UploadMs);
            _denoiseMs = Ewma(_denoiseMs, timings.DenoiseMs);
            _gradeMs = Ewma(_gradeMs, timings.ColourMs);
            _upscaleMs = Ewma(_upscaleMs, timings.UpscaleMs);
            _readbackMs = Ewma(_readbackMs, timings.ReadbackMs);

            if (++_framesSinceStats >= 30 || (now - _lastVramQuery).TotalSeconds > 2)
            {
                _framesSinceStats = 0;
                _lastVramQuery = now;
                _vramMb = engine.QueryVramUsedMb();
            }
        }
    }

    private static double Ewma(double current, double sample) =>
        current <= 0 ? sample : (current * 0.9) + (sample * 0.1);

    public void Reload()
    {
        // Configuration is pulled per frame; nothing to rebuild unless the engine failed.
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
